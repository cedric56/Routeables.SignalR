using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Routeables.Remote;

namespace Routeables.SignalR;

/// <summary>
/// The Routeables Remote Protocol over SignalR (PROTOCOL-REMOTE.md): the four calls of a replica, and
/// <see cref="LogAdvanced"/> sent to the clients of a collection when the log grows. A hub of its own, beside the hub of
/// the sessions: a client may replicate data without showing view models, and each endpoint has its own authorization.
/// </summary>
/// <remarks>
/// No session is kept between two calls: every call carries what it needs. A client that loses its connection
/// synchronizes again when it is back, from what it stored.
/// </remarks>
public class ReplicationHub(ReplicationServer server) : Hub
{
    /// <summary>The name of the client method that receives <see cref="LogAdvanced"/>.</summary>
    public const string LogAdvancedMethod = "LogAdvanced";

    private const string Joined = "rrp:joined";

    internal static string GroupOf(string collection) => "rrp:" + collection;

    private IReplicationChannel Channel => server.Connect(Context.User);

    public Task<RemoteWelcome> Hello(RemoteHello hello) => Channel.HelloAsync(hello, Context.ConnectionAborted);

    public Task<PushResult> Push(PushRequest request) => Channel.PushAsync(request, Context.ConnectionAborted);

    /// <summary>A pull also subscribes the connection to <see cref="LogAdvanced"/> for the collections it asks.</summary>
    public async Task<PullResult> Pull(PullRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Collections.Count > server.Options.MaxCollectionsPerPull)
            throw new HubException($"At most {server.Options.MaxCollectionsPerPull} collections per pull.");
        var joined = Context.Items.TryGetValue(Joined, out var value) && value is HashSet<string> set ? set : null;
        if (joined is null)
            Context.Items[Joined] = joined = new HashSet<string>(StringComparer.Ordinal);
        foreach (var collection in request.Collections)
            if (joined.Add(collection))
                await Groups.AddToGroupAsync(Context.ConnectionId, GroupOf(collection), Context.ConnectionAborted).ConfigureAwait(false);
        return await Channel.PullAsync(request, Context.ConnectionAborted).ConfigureAwait(false);
    }

    public Task<SnapshotResult> Snapshot(SnapshotRequest request) => Channel.SnapshotAsync(request, Context.ConnectionAborted);
}

/// <summary>Registers and maps the replication hub.</summary>
public static class ReplicationExtensions
{
    /// <summary>
    /// Registers the <see cref="ReplicationServer"/> the hub serves, and the notification of the connected clients when its
    /// log grows. <paramref name="create"/> builds it: its policy (who may write and read what), its store (memory by
    /// default; a database with Queryeables.Replication), its limits.
    /// </summary>
    public static IServiceCollection AddRouteablesReplication(this IServiceCollection services, Func<IServiceProvider, ReplicationServer>? create = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddSignalR();
        services.TryAddSingleton(provider =>
        {
            var server = create?.Invoke(provider) ?? new ReplicationServer();
            var hub = provider.GetRequiredService<IHubContext<ReplicationHub>>();
            // A hint to pull, never the data: a notification lost costs only delay, the next pull gets the changes.
            server.Admitted += (entry, documents) =>
            {
                foreach (var collection in documents.Select(d => d.Collection).Distinct(StringComparer.Ordinal))
                    _ = hub.Clients.Group(ReplicationHub.GroupOf(collection))
                        .SendAsync(ReplicationHub.LogAdvancedMethod, new LogAdvanced([collection], entry.Position));
            };
            return server;
        });
        return services;
    }

    /// <summary>
    /// Maps the replication hub. Protect it as any endpoint: <c>app.MapRouteablesReplication().RequireAuthorization()</c>; the
    /// policy of the server then decides per change and per document.
    /// </summary>
    public static HubEndpointConventionBuilder MapRouteablesReplication(this IEndpointRouteBuilder endpoints, string pattern = "/replication",
        Action<HttpConnectionDispatcherOptions>? options = null)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        return endpoints.MapHub<ReplicationHub>(pattern, options ?? (static _ => { }));
    }
}
