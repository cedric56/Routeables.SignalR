using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Maui.Storage;

namespace Routeables.SignalR.Essentials;

/// <summary>The options of Routeables.SignalR.Essentials: <c>AddRouteablesEssentials(options: o =&gt; ...)</c>.</summary>
public sealed class RouteablesEssentialsOptions
{
    /// <summary>
    /// Where the files of the clients go and come from (<c>MapRouteablesEssentials</c>): a picked file is uploaded there, a file shared or
    /// opened by a view model is downloaded from there. <c>/routeables/essentials</c> by default.
    /// </summary>
    public string Path { get; set; } = "/routeables/essentials";

    /// <summary>The largest file a client may upload (a picked file, a photo, a screenshot). 50 MB by default.</summary>
    public long MaxFileSize { get; set; } = 50L * 1024 * 1024;

    /// <summary>How many files one pick may bring. 20 by default.</summary>
    public int MaxFilesPerPick { get; set; } = 20;

    /// <summary>How long the link of a file the server gives the client (to share, to open, to attach) works. 10 minutes by default.</summary>
    public TimeSpan FileLinkLifetime { get; set; } = TimeSpan.FromMinutes(10);
}

/// <summary>A file picked or captured on the client and uploaded to the session: it is on disk, at <see cref="FileBase.FullPath"/>.</summary>
/// <remarks>
/// The portable Microsoft.Maui.Essentials a server runs on cannot open files itself (<c>FileBase.OpenReadAsync</c> throws): read it with
/// <see cref="OpenReadAsync"/> on this type, or <c>File.OpenRead(FullPath)</c>. It is deleted with the session.
/// </remarks>
public sealed class RemoteFileResult : FileResult
{
    /// <summary>A file of the session.</summary>
    public RemoteFileResult(string fullPath, string contentType, string fileName, long size) : base(fullPath, contentType)
    {
        ContentType = contentType;
        FileName = fileName;
        Size = size;
    }

    /// <summary>Its size, in bytes.</summary>
    public long Size { get; }

    /// <summary>Opens the file.</summary>
    public new Task<Stream> OpenReadAsync()
        => Task.FromResult<Stream>(new FileStream(FullPath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true));
}

/// <summary>
/// The files of a session: its directories (<c>FileSystem.CacheDirectory</c>, <c>AppDataDirectory</c>), what its client uploads, and the links
/// it gives its client. Deleted with the session. The links are random, short-lived, bound to the user of the session, and only reach the
/// file they were made for.
/// </summary>
public sealed class RemoteFiles : IDisposable
{
    private readonly RemoteFileLinks _links;
    private readonly RouteablesEssentialsOptions _options;
    private readonly RouteableConnection? _connection;
    private readonly ConcurrentDictionary<string, byte> _owned = new(StringComparer.Ordinal);
    private readonly Lazy<string> _root = new(() => System.IO.Path.Combine(System.IO.Path.GetTempPath(), "routeables-essentials", Guid.NewGuid().ToString("N")));
    private int _disposed;

    /// <summary>Creates the files of a session.</summary>
    public RemoteFiles(RemoteFileLinks links, RouteablesEssentialsOptions options, RouteableConnection? connection = null)
    {
        _links = links;
        _options = options;
        _connection = connection;
    }

    internal RouteablesEssentialsOptions Options => _options;

    /// <summary>A directory of the session (created).</summary>
    public string GetDirectory(string name)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        var path = System.IO.Path.Combine(_root.Value, name);
        Directory.CreateDirectory(path);
        return path;
    }

    private string? UserId => _connection?.User is { Identity.IsAuthenticated: true } user ? RemoteFileLinks.UserIdOf(user) : null;

    /// <summary>Accepts up to <paramref name="maxFiles"/> uploads, until the slot is disposed.</summary>
    internal UploadSlot OpenUpload(int maxFiles)
    {
        var slot = new UploadSlot(this, Math.Clamp(maxFiles, 1, Math.Max(1, _options.MaxFilesPerPick)), UserId);
        Own(_links.Add(slot));
        slot.Url = $"{_options.Path.TrimEnd('/')}/files/{slot.Token}";
        return slot;
    }

    /// <summary>A link the client downloads <paramref name="file"/> from, for <see cref="RouteablesEssentialsOptions.FileLinkLifetime"/>.</summary>
    internal object Link(FileBase file)
    {
        ArgumentNullException.ThrowIfNull(file);
        if (string.IsNullOrEmpty(file.FullPath) || !File.Exists(file.FullPath))
            throw new FileNotFoundException("The file to give to the client does not exist on the server.", file.FullPath);
        var contentType = ContentTypes.Of(file);
        var download = new Download(this, file.FullPath, contentType, SafeName(file.FileName), DateTimeOffset.UtcNow + _options.FileLinkLifetime, UserId);
        Own(_links.Add(download));
        return new { url = $"{_options.Path.TrimEnd('/')}/files/{download.Token}", name = download.FileName, contentType };
    }

    private void Own(string token) => _owned[token] = 0;

    internal void Release(string token)
    {
        _owned.TryRemove(token, out _);
        _links.Remove(token);
    }

    internal static string SafeName(string? name)
    {
        var fileName = (name ?? string.Empty).Replace('\\', '/');
        fileName = fileName[(fileName.LastIndexOf('/') + 1)..];
        var invalid = System.IO.Path.GetInvalidFileNameChars();
        fileName = new string(fileName.Select(c => invalid.Contains(c) || char.IsControl(c) ? '_' : c).ToArray()).Trim().TrimEnd('.');
        if (fileName.Length == 0 || fileName is "." or "..")
            fileName = "file";
        if (fileName.Length > 128)
        {
            var extension = System.IO.Path.GetExtension(fileName);
            if (extension.Length > 16)
                extension = string.Empty;
            fileName = fileName[..(128 - extension.Length)] + extension;
        }
        return fileName;
    }

    // A new file of the session; the name the client gave is kept as FileName, not as the path.
    internal string NewPath(string folder, string fileName)
        => System.IO.Path.Combine(GetDirectory(folder), Guid.NewGuid().ToString("N") + System.IO.Path.GetExtension(SafeName(fileName)));

    /// <inheritdoc />
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;
        foreach (var token in _owned.Keys)
            _links.Remove(token);
        _owned.Clear();
        if (_root.IsValueCreated)
        {
            try
            {
                if (Directory.Exists(_root.Value))
                    Directory.Delete(_root.Value, recursive: true);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // A file still open: the temp folder of the server is cleaned by its system.
            }
        }
    }
}

/// <summary>A link of a session to a file (one to download, or an upload slot).</summary>
internal abstract class RemoteFileLink
{
    protected RemoteFileLink(RemoteFiles owner, DateTimeOffset expires, string? userId)
    {
        Owner = owner;
        Expires = expires;
        UserId = userId;
        Token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    internal RemoteFiles Owner { get; }
    internal DateTimeOffset Expires { get; }
    internal string? UserId { get; }

    /// <summary>The random part of the URL.</summary>
    public string Token { get; }

    // The user of the session, when it has one, must be the one who comes: the token alone is not enough for an authenticated session.
    internal bool Allows(ClaimsPrincipal? user)
        => DateTimeOffset.UtcNow < Expires && (UserId is null || (user is { Identity.IsAuthenticated: true } && RemoteFileLinks.UserIdOf(user) == UserId));
}

internal sealed class Download(RemoteFiles owner, string path, string contentType, string fileName, DateTimeOffset expires, string? userId)
    : RemoteFileLink(owner, expires, userId)
{
    public string Path { get; } = path;
    public string ContentType { get; } = contentType;
    public string FileName { get; } = fileName;
}

/// <summary>Where the client uploads the files of one pick, while the call waits for them.</summary>
internal sealed class UploadSlot(RemoteFiles owner, int maxFiles, string? userId) : RemoteFileLink(owner, DateTimeOffset.MaxValue, userId), IDisposable
{
    private readonly ConcurrentDictionary<string, RemoteFileResult> _files = new(StringComparer.Ordinal);
    private int _reserved;

    public string Url { get; set; } = string.Empty;

    public int MaxFiles { get; } = maxFiles;

    public bool TryReserve()
    {
        if (Interlocked.Increment(ref _reserved) <= MaxFiles)
            return true;
        Interlocked.Decrement(ref _reserved);
        return false;
    }

    public void Unreserve() => Interlocked.Decrement(ref _reserved);

    public string Add(RemoteFileResult file)
    {
        var id = Convert.ToHexString(RandomNumberGenerator.GetBytes(12)).ToLowerInvariant();
        _files[id] = file;
        return id;
    }

    /// <summary>The files the client says it uploaded: only those of this slot, each once.</summary>
    public List<FileResult> Resolve(System.Text.Json.JsonElement ids)
    {
        var files = new List<FileResult>();
        foreach (var id in EssentialsJson.Array(ids, MaxFiles))
            if (id.ValueKind == System.Text.Json.JsonValueKind.String && _files.TryRemove(id.GetString()!, out var file))
                files.Add(file);
        return files;
    }

    public void Dispose() => Owner.Release(Token);
}

/// <summary>The links of every session (a singleton), and the endpoint the clients reach them by.</summary>
public sealed class RemoteFileLinks
{
    private readonly ConcurrentDictionary<string, RemoteFileLink> _links = new(StringComparer.Ordinal);

    internal string Add(RemoteFileLink link)
    {
        // Expired links are dropped as new ones come: their number stays bounded by what the sessions made in FileLinkLifetime.
        if (_links.Count > 0 && Random.Shared.Next(32) == 0)
            foreach (var (token, existing) in _links)
                if (existing.Expires <= DateTimeOffset.UtcNow)
                    _links.TryRemove(token, out _);
        _links[link.Token] = link;
        return link.Token;
    }

    internal void Remove(string token) => _links.TryRemove(token, out _);

    internal bool TryGet<T>(string token, ClaimsPrincipal? user, out T link) where T : RemoteFileLink
    {
        if (token.Length is > 0 and <= 64 && _links.TryGetValue(token, out var found) && found is T typed && typed.Allows(user))
        {
            link = typed;
            return true;
        }
        link = null!;
        return false;
    }

    internal static string? UserIdOf(ClaimsPrincipal user)
        => (user.FindFirst(ClaimTypes.NameIdentifier) ?? user.FindFirst("sub"))?.Value;

    // ---------------------------------------------------------------- endpoint

    internal static async Task UploadAsync(HttpContext context, string token)
    {
        var links = context.RequestServices.GetRequiredService<RemoteFileLinks>();
        if (!links.TryGet<UploadSlot>(token, context.User, out var slot))
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }
        var max = slot.Owner.Options.MaxFileSize;
        if (context.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } limit)
            limit.MaxRequestBodySize = max;
        if (context.Request.ContentLength > max)
        {
            context.Response.StatusCode = StatusCodes.Status413PayloadTooLarge;
            return;
        }
        if (!slot.TryReserve())
        {
            context.Response.StatusCode = StatusCodes.Status409Conflict;
            return;
        }

        var name = RemoteFiles.SafeName(Unescape(context.Request.Headers["X-File-Name"].ToString()));
        var contentType = ContentTypes.Clean(context.Request.ContentType) ?? ContentTypes.OfName(name);
        var path = slot.Owner.NewPath("uploads", name);
        long size = 0;
        try
        {
            await using (var target = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, useAsync: true))
            {
                var buffer = new byte[81920];
                int read;
                while ((read = await context.Request.Body.ReadAsync(buffer, context.RequestAborted).ConfigureAwait(false)) > 0)
                {
                    size += read;
                    if (size > max)
                        throw new InvalidDataException("too large");
                    await target.WriteAsync(buffer.AsMemory(0, read), context.RequestAborted).ConfigureAwait(false);
                }
            }
        }
        catch (Exception exception) when (exception is InvalidDataException or BadHttpRequestException or IOException or OperationCanceledException)
        {
            slot.Unreserve();
            try { File.Delete(path); } catch (IOException) { }
            if (!context.RequestAborted.IsCancellationRequested)
                context.Response.StatusCode = exception switch
                {
                    InvalidDataException => StatusCodes.Status413PayloadTooLarge,
                    BadHttpRequestException bad => bad.StatusCode,
                    _ => StatusCodes.Status400BadRequest,
                };
            return;
        }

        var id = slot.Add(new RemoteFileResult(path, contentType, name, size));
        await context.Response.WriteAsJsonAsync(new { file = id }).ConfigureAwait(false);
    }

    internal static async Task DownloadAsync(HttpContext context, string token)
    {
        var links = context.RequestServices.GetRequiredService<RemoteFileLinks>();
        if (!links.TryGet<Download>(token, context.User, out var download) || !File.Exists(download.Path))
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        // Served from the origin of the app: never rendered by the browser (an HTML or SVG file would run its scripts here). The client reads it
        // with fetch and builds the blob with the real type it received in the call.
        var headers = context.Response.Headers;
        headers.CacheControl = "no-store";
        headers.XContentTypeOptions = "nosniff";
        headers.ContentSecurityPolicy = "sandbox; default-src 'none'";
        var disposition = new ContentDispositionHeaderValue("attachment") { FileNameStar = download.FileName };
        headers.ContentDisposition = disposition.ToString();
        context.Response.ContentType = "application/octet-stream";
        await context.Response.SendFileAsync(download.Path, context.RequestAborted).ConfigureAwait(false);
    }

    private static string Unescape(string value)
    {
        try { return Uri.UnescapeDataString(value); }
        catch (UriFormatException) { return value; }
    }
}

/// <summary>The content types of the files that cross: the given one when it is well formed, the extension otherwise.</summary>
internal static class ContentTypes
{
    private static readonly Dictionary<string, string> ByExtension = new(StringComparer.OrdinalIgnoreCase)
    {
        [".png"] = "image/png", [".jpg"] = "image/jpeg", [".jpeg"] = "image/jpeg", [".gif"] = "image/gif", [".webp"] = "image/webp",
        [".bmp"] = "image/bmp", [".svg"] = "image/svg+xml", [".heic"] = "image/heic", [".pdf"] = "application/pdf", [".txt"] = "text/plain",
        [".csv"] = "text/csv", [".json"] = "application/json", [".xml"] = "application/xml", [".html"] = "text/html", [".htm"] = "text/html",
        [".zip"] = "application/zip", [".mp4"] = "video/mp4", [".webm"] = "video/webm", [".mov"] = "video/quicktime", [".mp3"] = "audio/mpeg",
        [".wav"] = "audio/wav", [".ogg"] = "audio/ogg", [".doc"] = "application/msword",
        [".docx"] = "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
        [".xlsx"] = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
        [".pptx"] = "application/vnd.openxmlformats-officedocument.presentationml.presentation",
    };

    public static string OfName(string? name)
        => ByExtension.TryGetValue(System.IO.Path.GetExtension(name ?? string.Empty), out var type) ? type : "application/octet-stream";

    // FileBase.ContentType asks the platform for the type of an extension: the portable build throws there.
    public static string Of(FileBase file)
    {
        try
        {
            return Clean(file.ContentType) ?? OfName(file.FullPath);
        }
        catch (Exception exception) when (exception is NotImplementedException or NotSupportedException or PlatformNotSupportedException)
        {
            return OfName(file.FullPath);
        }
    }

    public static string? Clean(string? contentType)
    {
        if (string.IsNullOrWhiteSpace(contentType) || contentType.Length > 128)
            return null;
        var type = contentType.Split(';')[0].Trim().ToLowerInvariant();
        var slash = type.IndexOf('/');
        return slash > 0 && slash < type.Length - 1 && type.IndexOf('/', slash + 1) < 0
               && type.All(c => char.IsAsciiLetterOrDigit(c) || c is '/' or '.' or '+' or '-')
            ? type
            : null;
    }
}

/// <summary>Maps the endpoint of the files of the clients.</summary>
public static class RemoteFileEndpoints
{
    /// <summary>
    /// Maps <c>POST</c> and <c>GET {Path}/files/{token}</c> (<see cref="RouteablesEssentialsOptions.Path"/>): the files the clients pick, capture
    /// or receive. Needed by <c>FilePicker</c>, <c>MediaPicker</c>, <c>Screenshot</c>, and the files given to <c>Share</c>, <c>Launcher</c> and
    /// <c>Email</c>. Protect it as the hub (<c>.RequireAuthorization()</c> on the group returned).
    /// </summary>
    public static RouteGroupBuilder MapRouteablesEssentials(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        var options = endpoints.ServiceProvider.GetService<RouteablesEssentialsOptions>() ?? new RouteablesEssentialsOptions();
        var group = endpoints.MapGroup(options.Path);
        group.MapPost("/files/{token}", (HttpContext context, string token) => RemoteFileLinks.UploadAsync(context, token)).DisableAntiforgery();
        group.MapGet("/files/{token}", (HttpContext context, string token) => RemoteFileLinks.DownloadAsync(context, token));
        return group;
    }
}
