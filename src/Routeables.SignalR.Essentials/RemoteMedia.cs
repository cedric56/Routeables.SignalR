using System.Reflection;
using System.Text.Json;
using Microsoft.Maui.ApplicationModel;
using Microsoft.Maui.Devices;
using Microsoft.Maui.Media;
using Essentieables;
using Microsoft.Maui.Storage;

namespace Routeables.SignalR.Essentials;

/// <summary>
/// <see cref="ITextToSpeech"/> of the client. Service <c>textToSpeech</c>; methods <c>getLocales()</c> returning
/// <c>[{ language, country, name, id }]</c>, <c>speak({ text, locale, pitch, volume, rate })</c> (ends when the speech ends) and
/// <c>cancel()</c> (when the token of <see cref="SpeakAsync"/> is cancelled).
/// </summary>
public sealed class RemoteTextToSpeech(RouteableEssentials essentials) : RemoteService(essentials, ServiceName), ITextToSpeech
{
    internal const string ServiceName = "textToSpeech";

    // Locale has no public constructor: Essentieables.Facades reaches it.
    private static Locale CreateLocale(string language, string country, string name, string id)
        => (Locale)EssentialsAccessors.CreateLocale(language, country, name, id);

    /// <inheritdoc />
    public async Task<IEnumerable<Locale>> GetLocalesAsync()
    {
        var result = await CallAsync("getLocales").ConfigureAwait(true);
        return [.. EssentialsJson.Array(result, 1000).Where(l => l.ValueKind == JsonValueKind.Object).Select(l => CreateLocale(
            EssentialsJson.String(l, "language", 32) ?? string.Empty,
            EssentialsJson.String(l, "country", 32) ?? string.Empty,
            EssentialsJson.String(l, "name", 256) ?? string.Empty,
            EssentialsJson.String(l, "id", 256) ?? string.Empty))];
    }

    /// <inheritdoc />
    public async Task SpeakAsync(string text, SpeechOptions? options = null, CancellationToken cancelToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(text);
        if (options?.Pitch is < 0 or > 2)
            throw new ArgumentOutOfRangeException(nameof(options), "The pitch is between 0 and 2.");
        if (options?.Volume is < 0 or > 1)
            throw new ArgumentOutOfRangeException(nameof(options), "The volume is between 0 and 1.");
        try
        {
            await CallAsync("speak", [new
            {
                text = EssentialsJson.Limit(text, 32_767),
                locale = options?.Locale is { } locale ? new { language = locale.Language, country = locale.Country, name = locale.Name, id = locale.Id } : null,
                pitch = options?.Pitch,
                volume = options?.Volume,
                rate = options?.Rate,
            }], EssentialsCallOptions.Interactive, cancelToken).ConfigureAwait(true);
        }
        catch (OperationCanceledException) when (cancelToken.IsCancellationRequested)
        {
            Post("cancel");
            throw;
        }
    }
}

/// <summary>What the file pickers of the client receive: the HTML <c>accept</c> filter, the title, and where to upload.</summary>
internal static class PickFilters
{
    // A server has no device platform: the browser entry is read first, then the other platforms' extensions and MIME types.
    private static readonly DevicePlatform Browser = DevicePlatform.Create("Browser");
    private static readonly DevicePlatform[] Others =
        [DevicePlatform.WinUI, DevicePlatform.Android, DevicePlatform.MacCatalyst, DevicePlatform.macOS, DevicePlatform.iOS, DevicePlatform.Tizen];

    // GetPlatformFileType is protected (part of the public API contract), unlike the dictionary behind it.
    private static readonly MethodInfo? PlatformFileType = typeof(FilePickerFileType).GetMethod(
        "GetPlatformFileType", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public, null, [typeof(DevicePlatform)], null);

    public static string? Accept(FilePickerFileType? types)
    {
        if (types is null)
            return null;
        var values = Of(types, Browser) ?? Others.SelectMany(p => Of(types, p) ?? []);
        var filters = values.Select(Normalize).OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        return filters.Count == 0 ? null : string.Join(",", filters);
    }

    private static IEnumerable<string>? Of(FilePickerFileType types, DevicePlatform platform)
    {
        try { return PlatformFileType?.Invoke(types, [platform]) as IEnumerable<string>; }
        catch (TargetInvocationException) { return null; }
    }

    /// <summary>".png", "png", "*.png" → ".png"; "image/png", "image/*" kept; anything else (a UTType such as "public.image") dropped.</summary>
    public static string? Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;
        value = value.Trim();
        if (value.StartsWith("*.", StringComparison.Ordinal))
            value = value[1..];
        if (value.Contains('/'))
            return value.Count(c => c == '/') == 1 && !value.Any(char.IsWhiteSpace) ? value.ToLowerInvariant() : null;
        if (!value.StartsWith('.'))
            value = "." + value;
        return value.Length > 1 && value.IndexOf('.', 1) < 0 && value.All(c => char.IsLetterOrDigit(c) || c is '.' or '-' or '_' or '+')
            ? value.ToLowerInvariant()
            : null;
    }
}

/// <summary>
/// <see cref="IFilePicker"/> of the client. Service <c>filePicker</c>; method <c>pick({ title, accept, multiple, upload })</c>: the client
/// uploads each file to <c>upload</c> (<c>POST</c>, the name in <c>X-File-Name</c>), and returns the ids the server answered, or null when
/// the user cancelled. The files are on the disk of the server (<see cref="RemoteFileResult"/>), deleted with the session.
/// Needs <c>MapRouteablesEssentials()</c>.
/// </summary>
public sealed class RemoteFilePicker(RouteableEssentials essentials, RemoteFiles files) : RemoteService(essentials, ServiceName), IFilePicker
{
    internal const string ServiceName = "filePicker";

    /// <inheritdoc />
    public async Task<FileResult?> PickAsync(PickOptions? options = null)
        => (await PickAsync(options?.PickerTitle, PickFilters.Accept(options?.FileTypes), 1).ConfigureAwait(true)).FirstOrDefault();

    /// <inheritdoc />
    public async Task<IEnumerable<FileResult>?> PickMultipleAsync(PickOptions? options = null)
        => await PickAsync(options?.PickerTitle, PickFilters.Accept(options?.FileTypes), files.Options.MaxFilesPerPick).ConfigureAwait(true);

    private Task<List<FileResult>> PickAsync(string? title, string? accept, int max)
        => RemotePick.RunAsync(files, max, upload => CallAsync("pick", [new { title, accept, multiple = max > 1, upload }], EssentialsCallOptions.Interactive));
}

/// <summary>A pick: an upload slot while the client chooses and uploads, then the files it says it uploaded.</summary>
internal static class RemotePick
{
    public static async Task<List<FileResult>> RunAsync(RemoteFiles files, int max, Func<string, Task<JsonElement>> call)
    {
        // The slot closes with the call: an upload that comes later is refused, the files not claimed are deleted with the session.
        using var slot = files.OpenUpload(max);
        var result = await call(slot.Url).ConfigureAwait(true);
        return EssentialsJson.IsNull(result) ? [] : slot.Resolve(result);
    }
}

/// <summary>
/// <see cref="IMediaPicker"/> of the client. Service <c>mediaPicker</c>; state <c>{ isCaptureSupported }</c>; methods
/// <c>pick({ title, kind: "photo" | "video", multiple, limit, upload })</c> and <c>capture({ kind, upload })</c>, which upload as
/// <see cref="RemoteFilePicker"/> does and return the ids (a capture: an array of one, or null when the user cancelled).
/// </summary>
public sealed class RemoteMediaPicker : RemoteService, IMediaPicker
{
    internal const string ServiceName = "mediaPicker";
    private readonly RemoteFiles _files;
    private bool _captureSupported;

    /// <summary>Creates the media picker of a session.</summary>
    public RemoteMediaPicker(RouteableEssentials essentials, RemoteFiles files) : base(essentials, ServiceName)
    {
        _files = files;
        Initialize();
    }

    /// <inheritdoc />
    protected override void OnReady() => _captureSupported = TryGetState(out var s) && (EssentialsJson.Bool(s, "isCaptureSupported") ?? false);

    /// <inheritdoc />
    public bool IsCaptureSupported => _captureSupported;

    private async Task<List<FileResult>> Pick(string kind, MediaPickerOptions? options, bool multiple)
    {
        var limit = multiple ? Math.Clamp(options?.SelectionLimit is > 0 ? options.SelectionLimit : _files.Options.MaxFilesPerPick, 1, _files.Options.MaxFilesPerPick) : 1;
        return await RemotePick.RunAsync(_files, limit,
            upload => CallAsync("pick", [new { title = options?.Title, kind, multiple, limit, upload }], EssentialsCallOptions.Interactive)).ConfigureAwait(true);
    }

    private async Task<FileResult?> Capture(string kind, MediaPickerOptions? options)
    {
        if (Essentials.HasAnnounced && !_captureSupported)
            throw new FeatureNotSupportedException("The device of this user cannot capture.");
        var files = await RemotePick.RunAsync(_files, 1,
            upload => CallAsync("capture", [new { title = options?.Title, kind, upload }], EssentialsCallOptions.Interactive)).ConfigureAwait(true);
        return files.FirstOrDefault();
    }

    /// <inheritdoc />
    [Obsolete("Switch to PickPhotosAsync which also allows multiple selections.")]
    public async Task<FileResult?> PickPhotoAsync(MediaPickerOptions? options = null) => (await Pick("photo", options, false).ConfigureAwait(true)).FirstOrDefault();

    /// <inheritdoc />
    public Task<List<FileResult>> PickPhotosAsync(MediaPickerOptions? options = null) => Pick("photo", options, true);

    /// <inheritdoc />
    public Task<FileResult?> CapturePhotoAsync(MediaPickerOptions? options = null) => Capture("photo", options);

    /// <inheritdoc />
    [Obsolete("Switch to PickVideosAsync which also allows multiple selections.")]
    public async Task<FileResult?> PickVideoAsync(MediaPickerOptions? options = null) => (await Pick("video", options, false).ConfigureAwait(true)).FirstOrDefault();

    /// <inheritdoc />
    public Task<List<FileResult>> PickVideosAsync(MediaPickerOptions? options = null) => Pick("video", options, true);

    /// <inheritdoc />
    public Task<FileResult?> CaptureVideoAsync(MediaPickerOptions? options = null) => Capture("video", options);
}

/// <summary>
/// <see cref="IScreenshot"/> of the client. Service <c>screenshot</c>; state <c>{ isCaptureSupported }</c>; method <c>capture({ upload })</c>
/// returning <c>{ file, width, height }</c>, the file (a PNG) uploaded as <see cref="RemoteFilePicker"/> does. A browser asks the user
/// what to share.
/// </summary>
public sealed class RemoteScreenshot : RemoteService, IScreenshot
{
    internal const string ServiceName = "screenshot";
    private readonly RemoteFiles _files;
    private bool _supported;

    /// <summary>Creates the screenshot of a session.</summary>
    public RemoteScreenshot(RouteableEssentials essentials, RemoteFiles files) : base(essentials, ServiceName)
    {
        _files = files;
        Initialize();
    }

    /// <inheritdoc />
    protected override void OnReady() => _supported = TryGetState(out var s) && (EssentialsJson.Bool(s, "isCaptureSupported") ?? false);

    /// <inheritdoc />
    public bool IsCaptureSupported => _supported;

    /// <inheritdoc />
    public async Task<IScreenshotResult> CaptureAsync()
    {
        if (Essentials.HasAnnounced && !_supported)
            throw new FeatureNotSupportedException("The client of this user cannot capture its screen.");
        using var slot = _files.OpenUpload(1);
        var result = await CallAsync("capture", [new { upload = slot.Url }], EssentialsCallOptions.Interactive).ConfigureAwait(true);
        var file = result.ValueKind == JsonValueKind.Object && result.TryGetProperty("file", out var id)
            ? slot.Resolve(JsonSerializer.SerializeToElement(new[] { id })).OfType<RemoteFileResult>().FirstOrDefault()
            : null;
        if (file is null)
            throw new OperationCanceledException("The screenshot was not taken.");
        return new RemoteScreenshotResult(file,
            (int)Math.Clamp(EssentialsJson.Double(result, "width") ?? 0, 0, 100_000),
            (int)Math.Clamp(EssentialsJson.Double(result, "height") ?? 0, 0, 100_000));
    }
}

/// <summary>A screenshot the client uploaded (PNG). The server does not convert images: asking for JPEG throws <see cref="NotSupportedException"/>.</summary>
public sealed class RemoteScreenshotResult(RemoteFileResult file, int width, int height) : IScreenshotResult
{
    /// <summary>The PNG file, on the disk of the server.</summary>
    public RemoteFileResult File { get; } = file;

    /// <inheritdoc />
    public int Width { get; } = width;

    /// <inheritdoc />
    public int Height { get; } = height;

    /// <inheritdoc />
    public Task<Stream> OpenReadAsync(ScreenshotFormat format = ScreenshotFormat.Png, int quality = 100)
    {
        if (format != ScreenshotFormat.Png)
            throw new NotSupportedException("The screenshot of a client is a PNG: the server does not convert it.");
        return File.OpenReadAsync();
    }

    /// <inheritdoc />
    public async Task CopyToAsync(Stream destination, ScreenshotFormat format = ScreenshotFormat.Png, int quality = 100)
    {
        ArgumentNullException.ThrowIfNull(destination);
        await using var source = await OpenReadAsync(format, quality).ConfigureAwait(true);
        await source.CopyToAsync(destination).ConfigureAwait(true);
    }
}
