using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace Routeables.SignalR;

/// <summary>
/// The traces and the metrics of Routeables.SignalR, under the name <see cref="Name"/>: add it to OpenTelemetry
/// (<c>.AddSource("Routeables.SignalR")</c>, <c>.AddMeter("Routeables.SignalR")</c>) or watch it with <c>dotnet-counters</c>.
/// </summary>
/// <remarks>
/// Metrics: <c>routeables.signalr.sessions</c> (attached sessions), <c>routeables.signalr.sessions.detached</c> (sessions waiting for their client),
/// <c>routeables.signalr.calls</c> and <c>routeables.signalr.call.duration</c> (the calls of the clients, by method and outcome),
/// <c>routeables.signalr.messages.sent</c>, <c>routeables.signalr.messages.coalesced</c>, <c>routeables.signalr.messages.dropped</c>,
/// <c>routeables.signalr.outbound.depth</c> (messages waiting for a client), <c>routeables.signalr.delivery.latency</c> (from the change to its
/// delivery to SignalR) and <c>routeables.signalr.projection.issues</c>. Every call is an activity named after the hub method.
/// </remarks>
public static class RouteablesTelemetry
{
    /// <summary>The name of the activity source and of the meter.</summary>
    public const string Name = "Routeables.SignalR";

    internal static readonly ActivitySource Source = new(Name);
    internal static readonly Meter Meter = new(Name);

    internal static readonly UpDownCounter<long> Sessions = Meter.CreateUpDownCounter<long>("routeables.signalr.sessions", "{session}", "Sessions attached to a connection.");
    internal static readonly UpDownCounter<long> DetachedSessions = Meter.CreateUpDownCounter<long>("routeables.signalr.sessions.detached", "{session}", "Sessions whose connection dropped, kept for the client to resume them.");
    internal static readonly Counter<long> DetachedEvicted = Meter.CreateCounter<long>("routeables.signalr.sessions.evicted", "{session}", "Sessions waiting for their client that were disposed to stay within MaxDetachedSessions or MaxDetachedSessionsPerUser.");
    internal static readonly Counter<long> Overflows = Meter.CreateCounter<long>("routeables.signalr.overflows", "{connection}", "Connections closed because their client let more than MaxPendingMessages wait.");
    internal static readonly Counter<long> Calls = Meter.CreateCounter<long>("routeables.signalr.calls", "{call}", "Calls of the clients.");
    internal static readonly Histogram<double> CallDuration = Meter.CreateHistogram<double>("routeables.signalr.call.duration", "ms", "How long a call took until the hub answered.");
    internal static readonly Counter<long> MessagesSent = Meter.CreateCounter<long>("routeables.signalr.messages.sent", "{message}", "Messages sent to the clients.");
    internal static readonly Counter<long> MessagesCoalesced = Meter.CreateCounter<long>("routeables.signalr.messages.coalesced", "{message}", "Messages merged into another one that was waiting.");
    internal static readonly Counter<long> MessagesDropped = Meter.CreateCounter<long>("routeables.signalr.messages.dropped", "{message}", "Messages that were not sent: stale, or no client.");
    internal static readonly Histogram<long> OutboundDepth = Meter.CreateHistogram<long>("routeables.signalr.outbound.depth", "{message}", "Messages waiting for a client when one more is added.");
    internal static readonly Histogram<double> DeliveryLatency = Meter.CreateHistogram<double>("routeables.signalr.delivery.latency", "ms", "From the moment a message is ready to the moment SignalR has written it.");
    internal static readonly Counter<long> ProjectionIssues = Meter.CreateCounter<long>("routeables.signalr.projection.issues", "{issue}", "Parts of a state that could not be sent.");
}
