namespace Routeables.SignalR;

/// <summary>
/// The thread of a session, as the UI thread is for a desktop window or the circuit for Blazor Server: one piece of work at a time, in the order
/// it was asked. The calls of the client, the events of the view models and the messages to the client go through it, so that a view model is
/// never used by two threads at once and its changes are told in the order they happened. An <c>await</c> in a view model comes back to it, as on
/// a desktop; work may interleave at an <c>await</c>, never in between.
/// </summary>
internal sealed class SessionDispatcher : SynchronizationContext
{
    private readonly object _gate = new();
    private readonly Queue<(SendOrPostCallback Callback, object? State, ExecutionContext? Context)> _queue = new();
    private readonly Action _endOfTurn;
    private readonly Action<Exception> _unhandled;
    private bool _running;
    private int _thread;

    /// <param name="endOfTurn">Called after each piece of work: the changes it made are told then, together.</param>
    /// <param name="unhandled">An exception that no task observes (an <c>async void</c> handler).</param>
    public SessionDispatcher(Action endOfTurn, Action<Exception> unhandled)
    {
        _endOfTurn = endOfTurn;
        _unhandled = unhandled;
    }

    /// <summary>The session this dispatcher runs: <see cref="RouteableSession.Running"/> while its work runs.</summary>
    internal RouteableSession? Session { get; init; }

    /// <summary>Whether the current thread is the one that runs the work of this session now.</summary>
    public bool CheckAccess() => Volatile.Read(ref _thread) == Environment.CurrentManagedThreadId;

    /// <inheritdoc />
    public override void Post(SendOrPostCallback d, object? state) => Enqueue(d, state, null, inline: false);

    /// <inheritdoc />
    public override void Send(SendOrPostCallback d, object? state)
    {
        if (CheckAccess())
        {
            d(state);
            return;
        }
        using var done = new ManualResetEventSlim();
        Exception? failure = null;
        Enqueue(_ =>
        {
            try { d(state); }
            catch (Exception exception) { failure = exception; }
            finally { done.Set(); }
        }, null, null, inline: false);
        done.Wait();
        if (failure is not null)
            throw new InvalidOperationException("The work sent to the session failed.", failure);
    }

    /// <inheritdoc />
    public override SynchronizationContext CreateCopy() => this;

    /// <summary>Runs <paramref name="work"/> on the session: now, on this thread, when the session is idle; after the work before it otherwise.</summary>
    public Task<T> InvokeAsync<T>(Func<Task<T>> work)
    {
        if (CheckAccess())
            return work();

        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        Enqueue(_ =>
        {
            Task<T> task;
            try { task = work(); }
            catch (Exception exception)
            {
                completion.TrySetException(exception);
                return;
            }
            task.ContinueWith(static (t, state) =>
            {
                var target = (TaskCompletionSource<T>)state!;
                if (t.IsCanceled) target.TrySetCanceled();
                else if (t.IsFaulted) target.TrySetException(t.Exception!.InnerExceptions);
                else target.TrySetResult(t.Result);
            }, completion, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }, null, ExecutionContext.Capture(), inline: true);
        return completion.Task;
    }

    /// <summary>Runs <paramref name="work"/> on the session.</summary>
    public Task<T> InvokeAsync<T>(Func<T> work) => InvokeAsync(() => Task.FromResult(work()));

    /// <summary>Runs <paramref name="work"/> on the session.</summary>
    public Task InvokeAsync(Action work) => InvokeAsync(() => { work(); return true; });

    /// <summary>Runs <paramref name="work"/> on the session now when the current thread is the session's, later otherwise; nothing waits for it.</summary>
    public void Run(Action work)
    {
        if (CheckAccess())
            work();
        else
            Enqueue(static state => ((Action)state!)(), work, ExecutionContext.Capture(), inline: false);
    }

    /// <summary>Completes once the work asked before this call has run (and the changes it made were told).</summary>
    public Task IdleAsync() => CheckAccess() ? Task.CompletedTask : InvokeAsync(static () => { });

    private void Enqueue(SendOrPostCallback callback, object? state, ExecutionContext? context, bool inline)
    {
        lock (_gate)
        {
            _queue.Enqueue((callback, state, context));
            if (_running)
                return;
            _running = true;
        }

        if (inline)
            Drain();
        else
            ThreadPool.UnsafeQueueUserWorkItem(static self => self.Drain(), this, preferLocal: false);
    }

    private void Drain()
    {
        var previous = Current;
        SetSynchronizationContext(this);
        Volatile.Write(ref _thread, Environment.CurrentManagedThreadId);
        try
        {
            while (true)
            {
                (SendOrPostCallback Callback, object? State, ExecutionContext? Context) item;
                lock (_gate)
                {
                    if (!_queue.TryDequeue(out item))
                    {
                        _running = false;
                        return;
                    }
                }

                try
                {
                    if (item.Context is null)
                    {
                        // A continuation (Post) restores the context it captured; set the session for the rest anyway, and restore it after.
                        var outer = RouteableSession.Ambient;
                        RouteableSession.Ambient = Session;
                        try { item.Callback(item.State); }
                        finally { RouteableSession.Ambient = outer; }
                    }
                    else
                        ExecutionContext.Run(item.Context, static s =>
                        {
                            var (session, c, st) = ((RouteableSession?, SendOrPostCallback, object?))s!;
                            // Flows into what the work starts (Task.Run, ConfigureAwait(false)); ExecutionContext.Run undoes it after.
                            RouteableSession.Ambient = session;
                            c(st);
                        }, (Session, item.Callback, item.State));
                }
                catch (Exception exception)
                {
                    _unhandled(exception);
                }

                // ExecutionContext.Run restores the context of the thread, not the synchronization context: the work stays on the session.
                SetSynchronizationContext(this);
                try { _endOfTurn(); }
                catch (Exception exception) { _unhandled(exception); }
            }
        }
        finally
        {
            Volatile.Write(ref _thread, 0);
            SetSynchronizationContext(previous);
        }
    }
}
