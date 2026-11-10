using System.Security.Claims;
using Routeables.Remote;
using Routeables.SignalR;

namespace Routeables.SignalR.E2E;

/// <summary>The replication hub of the end-to-end tests (tests/e2e/replication.test.mjs), with rules a test can see at work.</summary>
public static class E2EReplication
{
    public static IServiceCollection AddE2EReplication(this IServiceCollection services)
        => services.AddRouteablesReplication(_ => new ReplicationServer(new OwnerPolicy()));

    public static void MapE2EReplication(this WebApplication app) => app.MapRouteablesReplication().RequireAuthorization();

    /// <summary>A document with an "owner" is read and written by that user only; the name "forbidden" is refused.</summary>
    private sealed class OwnerPolicy : IReplicationPolicy
    {
        private static string Quoted(ClaimsPrincipal? user) => $"\"{user?.FindFirst(ClaimTypes.NameIdentifier)?.Value}\"";

        public async ValueTask<Admission> AdmitAsync(ClaimsPrincipal? user, Change change, Func<DocumentId, ValueTask<DocumentState?>> current, CancellationToken ct)
        {
            foreach (var op in change.Ops)
            {
                if (op.Op is SetField { Value: "\"forbidden\"" })
                    return Admission.Reject(RemoteErrorCodes.Invalid, "This value is not allowed.");
                if ((await current(op.Document))?.Get("owner") is { } owner && owner != Quoted(user))
                    return Admission.Reject(RemoteErrorCodes.Forbidden, "This document belongs to another user.");
            }
            return Admission.Accept;
        }

        public bool CanRead(ClaimsPrincipal? user, DocumentId document, DocumentState? state)
            => state?.Get("owner") is not { } owner || owner == Quoted(user);
    }
}
