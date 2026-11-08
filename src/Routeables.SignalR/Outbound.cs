using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace Routeables.SignalR;

/// <summary>
/// The messages of a session to its client, in one ordered stream. A message is sent once the one before it was written; while a slow client
/// lets messages wait, the changes of a target are merged into the one that waits, a message of an older page is dropped, and beyond
/// <see cref="RouteablesOptions.MaxPendingMessages"/> the connection is given up (<c>overflow</c>). The version of a change is given when it leaves,
/// so merged changes never leave a hole the client would take for a lost message. Without a client (the connection dropped), the state, the dialogs
/// and the ends of calls are not kept (the session sends them again when it is resumed, the ends of calls those the client says it waits for) and
/// the notifications wait, the last ones.
/// </summary>
internal sealed class Outbound(Func<string, long> generationOf, Action overflow, int maxPending, ILogger logger)
{
    private const int MaxDetachedMessages = 256;

    private readonly object _gate = new();
    private readonly LinkedList<Message> _queue = new();
    private readonly Queue<Message> _detached = new();
    private readonly Dictionary<string, (long Generation, long Version)> _versions = new(StringComparer.Ordinal);
    private IRouteableClient? _client;
    private bool _pumping;
    private bool _disposed;
    private TaskCompletionSource? _drained;

    /// <summary>The client that receives the messages, or <see langword="null"/> while the connection is gone.</summary>
    public void Attach(IRouteableClient? client)
    {
        List<Message> replay;
        lock (_gate)
        {
            _client = client;
            _versions.Clear();
            if (client is null)
                return;
            replay = [.. _detached];
            _detached.Clear();
        }
        foreach (var message in replay)
            Enqueue(message);
    }

    public void Navigated(RouteableNavigated message)
        => Enqueue(new Message(Kind.Navigated, message.Target, message.Generation, c => c.Navigated(message)));

    public void Changed(string target, long generation, List<RouteablePatch> patches)
        => Enqueue(new Message(Kind.Changed, target, generation, null) { Patches = patches });

    public void Closed(RouteableClosed message) => Enqueue(new Message(Kind.Closed, message.Target, message.Generation, c => c.Closed(message)));

    public void Failed(RouteableFailure message) => Enqueue(new Message(Kind.Other, null, 0, c => c.Failed(message)));

    public void Dialog(RouteableDialog message) => Enqueue(new Message(Kind.Dialog, null, 0, c => c.DialogRequested(message)));

    public void Toast(RouteableToast message) => Enqueue(new Message(Kind.Other, null, 0, c => c.Notified(message)));

    public void Completed(RouteableCompleted message) => Enqueue(new Message(Kind.Completed, null, 0, c => c.Completed(message)));

    /// <summary>Completes once every message given so far has been written (or dropped).</summary>
    public Task DrainedAsync()
    {
        lock (_gate)
        {
            if (!_pumping && _queue.Count == 0)
                return Task.CompletedTask;
            _drained ??= new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            return _drained.Task;
        }
    }

    public void Dispose()
    {
        TaskCompletionSource? drained;
        lock (_gate)
        {
            _disposed = true;
            _queue.Clear();
            _detached.Clear();
            drained = _drained;
            _drained = null;
        }
        drained?.TrySetResult();
    }

    private void Enqueue(Message message)
    {
        var start = false;
        lock (_gate)
        {
            if (_disposed)
                return;

            if (_client is null)
            {
                // The state is sent again when the session is resumed; a dialog too (it is still waiting); the end of a call too, when the client
                // says it waits for it (RouteableSession.TellEndsAsync): it is never dropped with the oldest notifications. The rest waits for the client.
                if (message.Kind is Kind.Other)
                {
                    if (_detached.Count >= MaxDetachedMessages)
                        _detached.Dequeue();
                    _detached.Enqueue(message);
                }
                else
                    RouteablesTelemetry.MessagesDropped.Add(1, new KeyValuePair<string, object?>("reason", "detached"));
                return;
            }

            if (message.Kind == Kind.Changed && TryMerge(message))
                return;

            _queue.AddLast(message);
            RouteablesTelemetry.OutboundDepth.Record(_queue.Count);
            if (_queue.Count > maxPending)
            {
                _queue.Clear();
                RouteablesTelemetry.Overflows.Add(1);
                RouteablesTelemetry.MessagesDropped.Add(maxPending, new KeyValuePair<string, object?>("reason", "overflow"));
                logger.LogWarning("More than {MaxPending} messages wait for the client: the connection is closed, the client resumes the session.", maxPending);
                ThreadPool.UnsafeQueueUserWorkItem(static o => ((Action)o!)(), overflow, preferLocal: false);
                return;
            }

            if (!_pumping)
                start = _pumping = true;
        }

        if (start)
            _ = PumpAsync();
    }

    // Merges the patches of a change into the change of the same page that waits, when no message about that target was queued after it.
    private bool TryMerge(Message message)
    {
        for (var node = _queue.Last; node is not null; node = node.Previous)
        {
            var queued = node.Value;
            if (queued.Target != message.Target)
                continue;
            if (queued.Kind != Kind.Changed || queued.Generation != message.Generation)
                return false;

            foreach (var patch in message.Patches!)
                RouteablePatches.Add(queued.Patches!, patch);
            RouteablesTelemetry.MessagesCoalesced.Add(1);
            return true;
        }
        return false;
    }

    // Sends the messages one after the other. It runs on the thread that queued the first one until a send does not complete at once.
    private async Task PumpAsync()
    {
        while (true)
        {
            Message message;
            IRouteableClient? client;
            lock (_gate)
            {
                if (_disposed || _queue.First is null)
                {
                    _pumping = false;
                    var drained = _drained;
                    _drained = null;
                    drained?.TrySetResult();
                    return;
                }
                message = _queue.First.Value;
                _queue.RemoveFirst();
                client = _client;
            }

            if (client is null || !Prepare(message, out var send))
            {
                RouteablesTelemetry.MessagesDropped.Add(1, new KeyValuePair<string, object?>("reason", client is null ? "detached" : "stale"));
                continue;
            }

            try
            {
                await send(client).ConfigureAwait(false);
                RouteablesTelemetry.MessagesSent.Add(1, new KeyValuePair<string, object?>("kind", message.Kind.ToString()));
                RouteablesTelemetry.DeliveryLatency.Record(Stopwatch.GetElapsedTime(message.Queued).TotalMilliseconds);
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                // A message is never retried out of order: the state is sent again when the session is resumed, the end of a call when the client
                // says it waits for it. A message that cannot be serialized is a fault of the application, not of the network: it is logged as such.
                var reason = exception switch
                {
                    System.Text.Json.JsonException or NotSupportedException => "serialization",
                    OperationCanceledException or ObjectDisposedException => "closed",
                    TimeoutException => "timeout",
                    _ => "transport",
                };
                if (reason == "serialization")
                    logger.LogError(exception, "A message could not be serialized for the client: it is not sent.");
                else
                    logger.LogDebug(exception, "A message could not be sent to the client ({Reason}).", reason);
                RouteablesTelemetry.MessagesDropped.Add(1, new KeyValuePair<string, object?>("reason", reason));
            }
        }
    }

    // Drops a message of a page that is no longer shown, and gives a change its version.
    private bool Prepare(Message message, out Func<IRouteableClient, Task> send)
    {
        send = message.Send!;
        if (message.Target is null)
            return true;

        var current = generationOf(message.Target);
        switch (message.Kind)
        {
            case Kind.Navigated:
                if (message.Generation < current)
                    return false;
                lock (_gate) _versions[message.Target] = (message.Generation, 0);
                return true;

            case Kind.Changed:
                if (message.Generation != current)
                    return false;
                long version;
                lock (_gate)
                {
                    if (!_versions.TryGetValue(message.Target, out var known) || known.Generation != message.Generation)
                        return false;   // the page it changes was not sent to this client
                    version = known.Version + 1;
                    _versions[message.Target] = (message.Generation, version);
                }
                var changed = new RouteableChanged(message.Target, message.Generation, version, message.Patches!);
                send = c => c.Changed(changed);
                return true;

            case Kind.Closed:
                lock (_gate) _versions.Remove(message.Target);
                return true;

            default:
                return true;
        }
    }

    private enum Kind { Navigated, Changed, Closed, Dialog, Completed, Other }

    private sealed class Message(Kind kind, string? target, long generation, Func<IRouteableClient, Task>? send)
    {
        public Kind Kind { get; } = kind;
        public string? Target { get; } = target;
        public long Generation { get; } = generation;
        public Func<IRouteableClient, Task>? Send { get; } = send;
        public List<RouteablePatch>? Patches { get; init; }
        public long Queued { get; } = Stopwatch.GetTimestamp();
    }
}

/// <summary>How patches of one target are merged: a later <c>set</c> replaces what was said of the same value or of a value inside it.</summary>
internal static class RouteablePatches
{
    public static void Add(List<RouteablePatch> patches, RouteablePatch patch)
    {
        if (patch.Op == RouteablePatch.Set)
            patches.RemoveAll(p => StartsWith(p.Path, patch.Path));
        patches.Add(patch);
    }

    // Whether path starts with prefix (or is it).
    public static bool StartsWith(IReadOnlyList<object> path, IReadOnlyList<object> prefix)
    {
        if (path.Count < prefix.Count)
            return false;
        for (var i = 0; i < prefix.Count; i++)
            if (!Equals(path[i], prefix[i]))
                return false;
        return true;
    }
}
