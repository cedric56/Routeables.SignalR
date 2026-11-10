using System.Text.Json;
using Microsoft.Maui.ApplicationModel;
using Microsoft.Maui.ApplicationModel.Communication;
using Microsoft.Maui.ApplicationModel.DataTransfer;
using Microsoft.Maui.Devices.Sensors;

namespace Routeables.SignalR.Essentials;

/// <summary><see cref="IPhoneDialer"/> of the client. Service <c>phoneDialer</c>; state <c>{ isSupported }</c>; method <c>open(number)</c>.</summary>
public sealed class RemotePhoneDialer : RemoteService, IPhoneDialer
{
    internal const string ServiceName = "phoneDialer";
    private bool _supported;

    /// <summary>Creates the phone dialer of a session.</summary>
    public RemotePhoneDialer(RouteableEssentials essentials) : base(essentials, ServiceName) => Initialize();

    /// <inheritdoc />
    protected override void OnReady() => _supported = TryGetState(out var s) && (EssentialsJson.Bool(s, "isSupported") ?? false);

    /// <inheritdoc />
    public bool IsSupported => _supported;

    /// <inheritdoc />
    public void Open(string number)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(number);
        if (Essentials.HasAnnounced && !_supported)
            throw new FeatureNotSupportedException("The device of this user cannot dial.");
        Post("open", [EssentialsJson.Limit(number.Trim(), 64)]);
    }
}

/// <summary><see cref="ISms"/> of the client. Service <c>sms</c>; state <c>{ isComposeSupported }</c>; method <c>compose({ body, recipients })</c>.</summary>
public sealed class RemoteSms : RemoteService, ISms
{
    internal const string ServiceName = "sms";
    private bool _supported;

    /// <summary>Creates the SMS of a session.</summary>
    public RemoteSms(RouteableEssentials essentials) : base(essentials, ServiceName) => Initialize();

    /// <inheritdoc />
    protected override void OnReady() => _supported = TryGetState(out var s) && (EssentialsJson.Bool(s, "isComposeSupported") ?? false);

    /// <inheritdoc />
    public bool IsComposeSupported => _supported;

    /// <inheritdoc />
    public Task ComposeAsync(SmsMessage? message)
    {
        if (Essentials.HasAnnounced && !_supported)
            throw new FeatureNotSupportedException("The device of this user cannot compose an SMS.");
        return CallAsync("compose", [new { body = message?.Body ?? string.Empty, recipients = message?.Recipients ?? [] }], EssentialsCallOptions.Interactive);
    }
}

/// <summary>
/// <see cref="IEmail"/> of the client. Service <c>email</c>; state <c>{ isComposeSupported }</c>; method
/// <c>compose({ subject, body, bodyFormat, to, cc, bcc, attachments: [{ url, name, contentType }] })</c>, the attachments being links to files
/// of the session (<c>MapRouteablesEssentials</c>).
/// </summary>
public sealed class RemoteEmail : RemoteService, IEmail
{
    internal const string ServiceName = "email";
    private readonly RemoteFiles _files;
    private bool _supported;

    /// <summary>Creates the email of a session.</summary>
    public RemoteEmail(RouteableEssentials essentials, RemoteFiles files) : base(essentials, ServiceName)
    {
        _files = files;
        Initialize();
    }

    /// <inheritdoc />
    protected override void OnReady() => _supported = TryGetState(out var s) && (EssentialsJson.Bool(s, "isComposeSupported") ?? false);

    /// <inheritdoc />
    public bool IsComposeSupported => _supported;

    /// <inheritdoc />
    public Task ComposeAsync(EmailMessage? message)
    {
        if (Essentials.HasAnnounced && !_supported)
            throw new FeatureNotSupportedException("The device of this user cannot compose an email.");
        var attachments = (message?.Attachments ?? []).Select(_files.Link).ToList();
        return CallAsync("compose", [new
        {
            subject = message?.Subject ?? string.Empty,
            body = message?.Body ?? string.Empty,
            bodyFormat = (message?.BodyFormat ?? EmailBodyFormat.PlainText).ToString(),
            to = message?.To ?? [],
            cc = message?.Cc ?? [],
            bcc = message?.Bcc ?? [],
            attachments,
        }], EssentialsCallOptions.Interactive);
    }
}

/// <summary>
/// <see cref="IContacts"/> of the client. Service <c>contacts</c>; method <c>select(multiple)</c> returning
/// <c>[{ id, namePrefix, givenName, middleName, familyName, nameSuffix, displayName, phones: [..], emails: [..] }]</c>.
/// </summary>
public sealed class RemoteContacts(RouteableEssentials essentials) : RemoteService(essentials, ServiceName), IContacts
{
    internal const string ServiceName = "contacts";

    /// <inheritdoc />
    public async Task<Contact?> PickContactAsync()
        => Parse(await CallAsync("select", [false], EssentialsCallOptions.Interactive).ConfigureAwait(true)).FirstOrDefault();

    /// <inheritdoc />
    public async Task<IEnumerable<Contact>> GetAllAsync(CancellationToken cancellationToken = default)
        => Parse(await CallAsync("select", [true], EssentialsCallOptions.Interactive, cancellationToken).ConfigureAwait(true));

    internal static List<Contact> Parse(JsonElement value)
        => [.. EssentialsJson.Array(value, 10_000).Where(c => c.ValueKind == JsonValueKind.Object).Select(c => new Contact(
            EssentialsJson.String(c, "id", 256) ?? string.Empty,
            EssentialsJson.String(c, "namePrefix", 256) ?? string.Empty,
            EssentialsJson.String(c, "givenName", 256) ?? string.Empty,
            EssentialsJson.String(c, "middleName", 256) ?? string.Empty,
            EssentialsJson.String(c, "familyName", 256) ?? string.Empty,
            EssentialsJson.String(c, "nameSuffix", 256) ?? string.Empty,
            EssentialsJson.Strings(c, "phones").Select(p => new ContactPhone(p)),
            EssentialsJson.Strings(c, "emails").Select(e => new ContactEmail(e)),
            EssentialsJson.String(c, "displayName", 512) ?? string.Empty))];
}

/// <summary><see cref="IBrowser"/> of the client. Service <c>browser</c>; method <c>open({ uri, launchMode })</c> returning whether it opened.</summary>
public sealed class RemoteBrowser(RouteableEssentials essentials) : RemoteService(essentials, ServiceName), IBrowser
{
    internal const string ServiceName = "browser";

    /// <inheritdoc />
    public async Task<bool> OpenAsync(Uri uri, BrowserLaunchOptions options)
    {
        ArgumentNullException.ThrowIfNull(uri);
        if (!uri.IsAbsoluteUri || uri.Scheme is not ("http" or "https"))
            throw new ArgumentException("A browser opens http and https addresses only.", nameof(uri));
        var result = await CallAsync("open", [new { uri = uri.AbsoluteUri, launchMode = (options?.LaunchMode ?? BrowserLaunchMode.SystemPreferred).ToString() }]).ConfigureAwait(true);
        return result.ValueKind == JsonValueKind.True;
    }
}

/// <summary>
/// <see cref="ILauncher"/> of the client. Service <c>launcher</c>; methods <c>canOpen(uri)</c>, <c>open(uri)</c>,
/// <c>openFile({ title, file: { url, name, contentType } })</c>, each returning a boolean. The server refuses the schemes that run code
/// (<c>javascript:</c>, <c>data:</c>, <c>blob:</c>, <c>file:</c>…) before asking the client.
/// </summary>
public sealed class RemoteLauncher(RouteableEssentials essentials, RemoteFiles files) : RemoteService(essentials, ServiceName), ILauncher
{
    internal const string ServiceName = "launcher";

    private static readonly string[] Refused = ["javascript", "vbscript", "data", "blob", "file", "about", "filesystem"];

    internal static bool IsRefused(Uri uri) => !uri.IsAbsoluteUri || Refused.Contains(uri.Scheme, StringComparer.OrdinalIgnoreCase);

    /// <inheritdoc />
    public async Task<bool> CanOpenAsync(Uri uri)
    {
        ArgumentNullException.ThrowIfNull(uri);
        if (IsRefused(uri))
            return false;
        return (await CallAsync("canOpen", [uri.AbsoluteUri]).ConfigureAwait(true)).ValueKind == JsonValueKind.True;
    }

    /// <inheritdoc />
    public async Task<bool> OpenAsync(Uri uri)
    {
        ArgumentNullException.ThrowIfNull(uri);
        if (IsRefused(uri))
            return false;
        return (await CallAsync("open", [uri.AbsoluteUri]).ConfigureAwait(true)).ValueKind == JsonValueKind.True;
    }

    /// <inheritdoc />
    public async Task<bool> OpenAsync(OpenFileRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.File is null)
            return false;
        var result = await CallAsync("openFile", [new { title = request.Title, file = files.Link(request.File) }], EssentialsCallOptions.Interactive).ConfigureAwait(true);
        return result.ValueKind == JsonValueKind.True;
    }

    /// <inheritdoc />
    public async Task<bool> TryOpenAsync(Uri uri)
    {
        try
        {
            return await CanOpenAsync(uri).ConfigureAwait(true) && await OpenAsync(uri).ConfigureAwait(true);
        }
        catch (Exception exception) when (exception is FeatureNotSupportedException or RouteableEssentialsException)
        {
            return false;
        }
    }
}

/// <summary>
/// <see cref="IMap"/> of the client. Service <c>map</c>; method
/// <c>open({ latitude, longitude, name, navigationMode, placemark? })</c> returning whether it opened.
/// </summary>
public sealed class RemoteMap(RouteableEssentials essentials) : RemoteService(essentials, ServiceName), IMap
{
    internal const string ServiceName = "map";

    private Task<JsonElement> Open(double latitude, double longitude, MapLaunchOptions? options, object? placemark)
    {
        if (!double.IsFinite(latitude) || latitude is < -90 or > 90)
            throw new ArgumentOutOfRangeException(nameof(latitude));
        if (!double.IsFinite(longitude) || longitude is < -180 or > 180)
            throw new ArgumentOutOfRangeException(nameof(longitude));
        return CallAsync("open", [new
        {
            latitude,
            longitude,
            name = options?.Name,
            navigationMode = (options?.NavigationMode ?? NavigationMode.None).ToString(),
            placemark,
        }]);
    }

    private static object Describe(Placemark placemark) => new
    {
        thoroughfare = placemark.Thoroughfare,
        subThoroughfare = placemark.SubThoroughfare,
        locality = placemark.Locality,
        subLocality = placemark.SubLocality,
        adminArea = placemark.AdminArea,
        subAdminArea = placemark.SubAdminArea,
        postalCode = placemark.PostalCode,
        countryName = placemark.CountryName,
        countryCode = placemark.CountryCode,
        featureName = placemark.FeatureName,
    };

    /// <inheritdoc />
    public Task OpenAsync(double latitude, double longitude, MapLaunchOptions options) => Open(latitude, longitude, options, null);

    /// <inheritdoc />
    public Task OpenAsync(Placemark placemark, MapLaunchOptions options)
    {
        ArgumentNullException.ThrowIfNull(placemark);
        return Open(placemark.Location?.Latitude ?? 0, placemark.Location?.Longitude ?? 0, options, Describe(placemark));
    }

    /// <inheritdoc />
    public async Task<bool> TryOpenAsync(double latitude, double longitude, MapLaunchOptions options)
    {
        try
        {
            await OpenAsync(latitude, longitude, options).ConfigureAwait(true);
            return true;
        }
        catch (Exception exception) when (exception is FeatureNotSupportedException or RouteableEssentialsException)
        {
            return false;
        }
    }

    /// <inheritdoc />
    public async Task<bool> TryOpenAsync(Placemark placemark, MapLaunchOptions options)
    {
        try
        {
            await OpenAsync(placemark, options).ConfigureAwait(true);
            return true;
        }
        catch (Exception exception) when (exception is FeatureNotSupportedException or RouteableEssentialsException)
        {
            return false;
        }
    }
}

/// <summary>
/// <see cref="IShare"/> of the client. Service <c>share</c>; methods <c>shareText({ title, text, subject, uri })</c> and
/// <c>shareFiles({ title, files: [{ url, name, contentType }] })</c>, the files being links to files of the session.
/// </summary>
public sealed class RemoteShare(RouteableEssentials essentials, RemoteFiles files) : RemoteService(essentials, ServiceName), IShare
{
    internal const string ServiceName = "share";

    /// <inheritdoc />
    public Task RequestAsync(ShareTextRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrEmpty(request.Text) && string.IsNullOrEmpty(request.Uri))
            throw new ArgumentException("Nothing to share: the text and the uri are empty.", nameof(request));
        return CallAsync("shareText", [new { title = request.Title, text = request.Text, subject = request.Subject, uri = request.Uri }], EssentialsCallOptions.Interactive);
    }

    /// <inheritdoc />
    public Task RequestAsync(ShareFileRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.File);
        return ShareFiles(request.Title, [request.File]);
    }

    /// <inheritdoc />
    public Task RequestAsync(ShareMultipleFilesRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Files is not { Count: > 0 })
            throw new ArgumentException("No file to share.", nameof(request));
        return ShareFiles(request.Title, request.Files);
    }

    private Task ShareFiles(string? title, IEnumerable<ShareFile> shared)
        => CallAsync("shareFiles", [new { title, files = shared.Select(files.Link).ToList() }], EssentialsCallOptions.Interactive);
}

/// <summary>
/// <see cref="IClipboard"/> of the client. Service <c>clipboard</c>; state <c>{ hasText }</c>; methods <c>getText()</c>, <c>setText(text)</c>;
/// event <c>changed</c> (<c>{ hasText }</c>), asked while somebody listens to <see cref="ClipboardContentChanged"/>.
/// </summary>
public sealed class RemoteClipboard : RemoteService, IClipboard
{
    internal const string ServiceName = "clipboard";
    private readonly object _gate = new();
    private EventHandler<EventArgs>? _changed;
    private IDisposable? _subscription;
    private bool _hasText;

    /// <summary>Creates the clipboard of a session.</summary>
    public RemoteClipboard(RouteableEssentials essentials) : base(essentials, ServiceName) => Initialize();

    /// <inheritdoc />
    protected override void OnReady() => _hasText = TryGetState(out var s) && (EssentialsJson.Bool(s, "hasText") ?? false);

    /// <inheritdoc />
    public bool HasText => _hasText;

    /// <inheritdoc />
    public async Task<string?> GetTextAsync()
    {
        // Reading the clipboard asks the user in a browser.
        var text = await CallAsync("getText", options: EssentialsCallOptions.Interactive).ConfigureAwait(true);
        var value = text.ValueKind == JsonValueKind.String ? text.GetString() : null;
        _hasText = !string.IsNullOrEmpty(value);
        return value;
    }

    /// <inheritdoc />
    public async Task SetTextAsync(string? text)
    {
        await CallAsync("setText", [text ?? string.Empty], EssentialsCallOptions.Interactive).ConfigureAwait(true);
        var had = _hasText;
        _hasText = !string.IsNullOrEmpty(text);
        if (had != _hasText || _hasText)
            _changed?.Invoke(this, EventArgs.Empty);
    }

    /// <inheritdoc />
    public event EventHandler<EventArgs> ClipboardContentChanged
    {
        add
        {
            lock (_gate)
            {
                _changed += value;
                _subscription ??= Subscribe("changed", payload =>
                {
                    if (payload is { ValueKind: JsonValueKind.Object } p)
                        _hasText = EssentialsJson.Bool(p, "hasText") ?? _hasText;
                    _changed?.Invoke(this, EventArgs.Empty);
                });
            }
        }
        remove
        {
            lock (_gate)
            {
                _changed -= value;
                if (_changed is null)
                {
                    _subscription?.Dispose();
                    _subscription = null;
                }
            }
        }
    }
}
