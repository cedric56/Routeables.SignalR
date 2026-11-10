using System.Text.Json;
using Microsoft.Maui.ApplicationModel;
using Microsoft.Maui.Authentication;

namespace Routeables.SignalR.Essentials;

/// <summary>
/// <see cref="IWebAuthenticator"/> of the client. Service <c>webAuthenticator</c>; method
/// <c>authenticate({ url, callbackUrl, prefersEphemeralWebBrowserSession })</c> returning the URL the flow came back to. The browser opens
/// <c>url</c> in a popup and waits for it to reach <c>callbackUrl</c> (a page of the same origin that posts its address to the opener).
/// </summary>
/// <remarks>
/// The answer of the client is checked: the address must be the callback URL that was asked (same scheme, host, port and path), so a client
/// cannot make the view model accept another one. Validating the <c>state</c> of OAuth and exchanging the code stay the application's job.
/// </remarks>
public sealed class RemoteWebAuthenticator(RouteableEssentials essentials) : RemoteService(essentials, ServiceName), IWebAuthenticator
{
    internal const string ServiceName = "webAuthenticator";

    /// <inheritdoc />
    public Task<WebAuthenticatorResult> AuthenticateAsync(WebAuthenticatorOptions webAuthenticatorOptions)
        => AuthenticateAsync(webAuthenticatorOptions, CancellationToken.None);

    /// <inheritdoc />
    public async Task<WebAuthenticatorResult> AuthenticateAsync(WebAuthenticatorOptions webAuthenticatorOptions, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(webAuthenticatorOptions);
        var url = webAuthenticatorOptions.Url ?? throw new ArgumentException("The authentication URL is missing.", nameof(webAuthenticatorOptions));
        var callback = webAuthenticatorOptions.CallbackUrl ?? throw new ArgumentException("The callback URL is missing.", nameof(webAuthenticatorOptions));
        if (!url.IsAbsoluteUri || url.Scheme is not ("https" or "http"))
            throw new ArgumentException("The authentication URL must be an absolute http(s) URL.", nameof(webAuthenticatorOptions));

        var result = await CallAsync("authenticate", [new
        {
            url = url.AbsoluteUri,
            callbackUrl = callback.IsAbsoluteUri ? callback.AbsoluteUri : callback.OriginalString,
            prefersEphemeralWebBrowserSession = webAuthenticatorOptions.PrefersEphemeralWebBrowserSession,
        }], EssentialsCallOptions.Interactive, cancellationToken).ConfigureAwait(true);

        if (result.ValueKind != JsonValueKind.String || !Uri.TryCreate(result.GetString(), UriKind.Absolute, out var returned))
            throw new TaskCanceledException("The authentication did not complete.");
        if (!IsCallback(returned, callback))
            throw new UnauthorizedAccessException("The client came back to another address than the callback URL.");

        return webAuthenticatorOptions.ResponseDecoder is { } decoder
            ? new WebAuthenticatorResult(returned, decoder)
            : new WebAuthenticatorResult(returned);
    }

    internal static bool IsCallback(Uri returned, Uri callback)
    {
        if (!callback.IsAbsoluteUri)
            return returned.AbsolutePath.Equals(callback.OriginalString.Split('?', '#')[0], StringComparison.Ordinal);
        return string.Equals(returned.Scheme, callback.Scheme, StringComparison.OrdinalIgnoreCase)
            && string.Equals(returned.Host, callback.Host, StringComparison.OrdinalIgnoreCase)
            && returned.Port == callback.Port
            && returned.AbsolutePath.Equals(callback.AbsolutePath, StringComparison.Ordinal);
    }
}

/// <summary>
/// <see cref="IAppActions"/> of the client (the shortcuts of an installed app). Service <c>appActions</c>; state <c>{ isSupported }</c>;
/// methods <c>get()</c> and <c>set(actions)</c>, an action being <c>{ id, title, subtitle, icon }</c>; event <c>activated</c> (an action).
/// A browser has none: <see cref="IsSupported"/> is false and the calls throw <see cref="FeatureNotSupportedException"/>.
/// </summary>
public sealed class RemoteAppActions : RemoteService, IAppActions
{
    internal const string ServiceName = "appActions";
    private bool _supported;

    /// <summary>Creates the app actions of a session.</summary>
    public RemoteAppActions(RouteableEssentials essentials) : base(essentials, ServiceName)
    {
        Subscribe("activated", payload =>
        {
            if (payload is { ValueKind: JsonValueKind.Object } p && Parse(p) is { } action)
                AppActionActivated?.Invoke(this, new AppActionEventArgs(action));
        });
        Initialize();
    }

    /// <inheritdoc />
    protected override void OnReady() => _supported = TryGetState(out var s) && (EssentialsJson.Bool(s, "isSupported") ?? false);

    /// <inheritdoc />
    public bool IsSupported => _supported;

    /// <inheritdoc />
    public event EventHandler<AppActionEventArgs>? AppActionActivated;

    /// <inheritdoc />
    public async Task<IEnumerable<AppAction>> GetAsync()
    {
        Ensure();
        var result = await CallAsync("get").ConfigureAwait(true);
        return [.. EssentialsJson.Array(result, 100).Select(Parse).OfType<AppAction>()];
    }

    /// <inheritdoc />
    public Task SetAsync(IEnumerable<AppAction> actions)
    {
        ArgumentNullException.ThrowIfNull(actions);
        Ensure();
        return CallAsync("set", [actions.Select(a => new { id = a.Id, title = a.Title, subtitle = a.Subtitle, icon = IconOf(a) }).ToList()]);
    }

    // AppAction.Icon is internal in MAUI.
    [System.Runtime.CompilerServices.UnsafeAccessor(System.Runtime.CompilerServices.UnsafeAccessorKind.Method, Name = "get_Icon")]
    private static extern string? IconOf(AppAction action);

    private void Ensure()
    {
        if (Essentials.HasAnnounced && !_supported)
            throw new FeatureNotSupportedException("The client of this user has no app actions.");
    }

    private static AppAction? Parse(JsonElement value)
        => EssentialsJson.String(value, "id", 256) is { Length: > 0 } id && EssentialsJson.String(value, "title", 256) is { Length: > 0 } title
            ? new AppAction(id, title, EssentialsJson.String(value, "subtitle", 256) ?? string.Empty, EssentialsJson.String(value, "icon", 256) ?? string.Empty)
            : null;
}
