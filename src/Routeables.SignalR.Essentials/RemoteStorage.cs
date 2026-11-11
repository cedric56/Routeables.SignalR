using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.FileProviders;
using Microsoft.Maui.ApplicationModel;
using Microsoft.Maui.Storage;

namespace Routeables.SignalR.Essentials;

/// <summary>
/// <see cref="ISecureStorage"/> of the client (in a browser: AES-GCM with a key that cannot be exported). Service <c>secureStorage</c>; state
/// <c>{ keys: [..] }</c>: the keys only, never the values; methods <c>get(key)</c>, <c>set(key, value)</c>, <c>remove(key)</c>, <c>removeAll()</c>.
/// </summary>
/// <remarks>
/// A value read is in the memory of the server while the view model holds it, as on any server. <c>Remove</c> and <c>RemoveAll</c> are
/// synchronous in MAUI: they are sent in the background, in order, and again after a reconnection; <see cref="FlushAsync"/> waits for them.
/// </remarks>
public sealed class RemoteSecureStorage : RemoteService, ISecureStorage
{
    internal const string ServiceName = "secureStorage";
    private static readonly EssentialsCallOptions Background = new() { Replay = true, Ask = false };

    private readonly ConcurrentDictionary<string, byte> _keys = new(StringComparer.Ordinal);
    private readonly object _gate = new();
    private readonly HashSet<Task> _pending = [];
    private readonly ConcurrentQueue<Exception> _errors = new();

    /// <summary>Creates the secure storage of a session.</summary>
    public RemoteSecureStorage(RouteableEssentials essentials) : base(essentials, ServiceName) => Initialize();

    /// <inheritdoc />
    protected override void OnReady()
    {
        if (!TryGetState(out var state))
            return;
        _keys.Clear();
        foreach (var key in EssentialsJson.Strings(state, "keys", 10_000))
            _keys[key] = 0;
    }

    /// <inheritdoc />
    public async Task<string?> GetAsync(string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        var value = await CallAsync("get", [key]).ConfigureAwait(true);
        if (value.ValueKind != JsonValueKind.String)
        {
            _keys.TryRemove(key, out _);
            return null;
        }
        _keys[key] = 0;
        return value.GetString();
    }

    /// <inheritdoc />
    public async Task SetAsync(string key, string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentNullException.ThrowIfNull(value);
        // Waited for: the caller must not believe a secret is stored when it is not.
        await CallAsync("set", [key, value], EssentialsCallOptions.Replayable).ConfigureAwait(true);
        _keys[key] = 0;
    }

    /// <inheritdoc />
    public bool Remove(string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        var known = _keys.TryRemove(key, out _);
        Track(Essentials.InvokeAsync(Service, "remove", [key], Background));   // also what another tab wrote
        return known;
    }

    /// <inheritdoc />
    public void RemoveAll()
    {
        _keys.Clear();
        Track(Essentials.InvokeAsync(Service, "removeAll", null, Background));
    }

    private void Track(Task operation)
    {
        // FlushAsync waits for the record of the outcome, not for the operation: two continuations of one task run in no given order, so
        // waiting for the operation alone could look at the failures before this one is recorded.
        var recorded = operation.ContinueWith(t =>
        {
            if (t.Exception is { } exception)
                _errors.Enqueue(exception.InnerException ?? exception);
        }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        lock (_gate)
            _pending.Add(recorded);
        _ = recorded.ContinueWith(t =>
        {
            lock (_gate)
                _pending.Remove(t);
        }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    /// <summary>Waits for the removals made so far, and throws if any failed since the last call (a sign-out that must be complete).</summary>
    public async Task FlushAsync()
    {
        Task[] pending;
        lock (_gate)
            pending = [.. _pending];
        try { await Task.WhenAll(pending).ConfigureAwait(true); }
        catch { /* collected below */ }
        var errors = new List<Exception>();
        while (_errors.TryDequeue(out var error))
            errors.Add(error);
        if (errors.Count > 0)
            throw new AggregateException("One or more secure storage removals failed on the client.", errors);
    }
}

/// <summary>
/// <see cref="IFileSystem"/> of a session, on the server: <see cref="CacheDirectory"/> and <see cref="AppDataDirectory"/> are directories of the
/// session (deleted with it, never shared with another user); the app package is the web root of the application (<c>wwwroot</c>). Nothing
/// goes to the client: a browser has no file system to give.
/// </summary>
public sealed class RemoteFileSystem(RemoteFiles files, IWebHostEnvironment? environment = null) : IFileSystem
{
    /// <inheritdoc />
    public string CacheDirectory => files.GetDirectory("cache");

    /// <inheritdoc />
    public string AppDataDirectory => files.GetDirectory("appdata");

    private IFileInfo Find(string filename)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filename);
        var provider = environment?.WebRootFileProvider ?? new NullFileProvider();
        // The provider refuses what leaves its root (".."); the separators of the platform are normalized.
        return provider.GetFileInfo(filename.Replace('\\', '/').TrimStart('/'));
    }

    /// <inheritdoc />
    public Task<Stream> OpenAppPackageFileAsync(string filename)
    {
        var file = Find(filename);
        if (!file.Exists || file.IsDirectory)
            throw new FileNotFoundException($"'{filename}' is not in the web root of the application.", filename);
        return Task.FromResult(file.CreateReadStream());
    }

    /// <inheritdoc />
    public Task<bool> AppPackageFileExistsAsync(string filename)
    {
        var file = Find(filename);
        return Task.FromResult(file.Exists && !file.IsDirectory);
    }
}

/// <summary>
/// <see cref="IVersionTracking"/> of the client, as MAUI computes it: the versions and builds of the app (<see cref="IAppInfo"/> of the client)
/// that the client already ran, kept in its preferences (<see cref="IPreferences"/> of the client), under the same keys as MAUI. Tracked once
/// the client announced itself.
/// </summary>
public sealed class RemoteVersionTracking(IPreferences preferences, IAppInfo appInfo) : IVersionTracking
{
    private const string VersionsKey = "VersionTracking.Versions";
    private const string BuildsKey = "VersionTracking.Builds";

    private readonly object _gate = new();
    private List<string>? _versions;
    private List<string>? _builds;
    private bool _firstEver;
    private bool _firstForVersion;
    private bool _firstForBuild;

    private string SharedName => $"{appInfo.PackageName}.microsoft.maui.essentials.versiontracking";

    /// <inheritdoc />
    public void Track()
    {
        lock (_gate)
        {
            if (_versions is not null)
                return;

            _firstEver = !preferences.ContainsKey(VersionsKey, SharedName) || !preferences.ContainsKey(BuildsKey, SharedName);
            _versions = Read(VersionsKey);
            _builds = Read(BuildsKey);

            if (_firstEver)
            {
                _versions.Clear();
                _builds.Clear();
            }

            // As MAUI: the first launch of a version is also a return to an older one, which moves to the end of the history.
            _firstForVersion = !_versions.Contains(CurrentVersion) || CurrentVersion != _versions.LastOrDefault();
            if (_firstForVersion)
            {
                _versions.RemoveAll(v => v == CurrentVersion);
                _versions.Add(CurrentVersion);
            }
            _firstForBuild = !_builds.Contains(CurrentBuild) || CurrentBuild != _builds.LastOrDefault();
            if (_firstForBuild)
            {
                _builds.RemoveAll(b => b == CurrentBuild);
                _builds.Add(CurrentBuild);
            }

            if (_firstForVersion || _firstForBuild)
            {
                preferences.Set(VersionsKey, string.Join('|', _versions), SharedName);
                preferences.Set(BuildsKey, string.Join('|', _builds), SharedName);
            }
        }
    }

    private List<string> Read(string key)
        => [.. (preferences.Get<string?>(key, null, SharedName) ?? string.Empty).Split('|', StringSplitOptions.RemoveEmptyEntries)];

    private (List<string> Versions, List<string> Builds) Tracked()
    {
        Track();
        return (_versions!, _builds!);
    }

    /// <inheritdoc />
    public bool IsFirstLaunchEver { get { Track(); return _firstEver; } }
    /// <inheritdoc />
    public bool IsFirstLaunchForCurrentVersion { get { Track(); return _firstForVersion; } }
    /// <inheritdoc />
    public bool IsFirstLaunchForCurrentBuild { get { Track(); return _firstForBuild; } }
    /// <inheritdoc />
    public string CurrentVersion => appInfo.VersionString;
    /// <inheritdoc />
    public string CurrentBuild => appInfo.BuildString;
    /// <inheritdoc />
    public string? PreviousVersion => Previous(Tracked().Versions);
    /// <inheritdoc />
    public string? PreviousBuild => Previous(Tracked().Builds);
    /// <inheritdoc />
    public string? FirstInstalledVersion => Tracked().Versions.FirstOrDefault();
    /// <inheritdoc />
    public string? FirstInstalledBuild => Tracked().Builds.FirstOrDefault();
    /// <inheritdoc />
    public IReadOnlyList<string> VersionHistory => [.. Tracked().Versions];
    /// <inheritdoc />
    public IReadOnlyList<string> BuildHistory => [.. Tracked().Builds];

    /// <inheritdoc />
    public bool IsFirstLaunchForVersion(string version) => CurrentVersion == version && IsFirstLaunchForCurrentVersion;

    /// <inheritdoc />
    public bool IsFirstLaunchForBuild(string build) => CurrentBuild == build && IsFirstLaunchForCurrentBuild;

    // As MAUI: the one before the last of the history (the current one is last), or null.
    private static string? Previous(List<string> history) => history.Count >= 2 ? history[^2] : null;
}
