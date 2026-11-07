using System.Collections.Concurrent;
using Routeables.Services;

namespace Routeables.SignalR;

/// <summary>
/// What the dialogs, the notifications and the modals of a connection share with its session. On a desktop a dialog is a window and the
/// view model awaits it; here the view model awaits too, but the dialog is sent to the browser and the answer comes back by the hub
/// (<c>Answer</c>). One per connection (scoped). A dialog that waits survives a reconnection: it is sent again when the session is resumed.
/// </summary>
public sealed class RemoteInteractions : IDisposable
{
    private sealed record Pending(RouteableDialog Dialog, TaskCompletionSource<int> Answer, long Order);

    private sealed record Toast(Action? OnClick, Action? OnClose);

    private readonly ConcurrentDictionary<string, Pending> _dialogs = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<int, Toast> _toasts = new();
    private int _nextDialog;
    private int _nextToast;

    /// <summary>A dialog has to be shown.</summary>
    public event Action<RouteableDialog>? DialogRequested;

    /// <summary>A notification has to be shown.</summary>
    public event Action<RouteableToast>? ToastRequested;

    /// <summary>A modal view model is open: the client shows it as a page of the target <see cref="RouteableTargets.Modal"/>.</summary>
    public event Action<object>? ModalOpened;

    /// <summary>The modal view model closed.</summary>
    public event Action? ModalClosed;

    /// <summary>How many dialogs wait for an answer.</summary>
    public int PendingCount => _dialogs.Count;

    /// <summary>The dialogs that wait for an answer, in the order they were asked.</summary>
    public IReadOnlyList<RouteableDialog> PendingDialogs => [.. _dialogs.Values.OrderBy(p => p.Order).Select(p => p.Dialog)];

    /// <summary>Shows a dialog and completes with the index of the button that was chosen.</summary>
    public async Task<int> AskAsync(string kind, string title, string? header, string? body, string? footer, bool showProgress, IReadOnlyList<string> buttons)
    {
        var order = Interlocked.Increment(ref _nextDialog);
        var id = "d" + order.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var dialog = new RouteableDialog(id, kind, title, header, body, footer, showProgress, buttons);
        var pending = new Pending(dialog, new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously), order);
        _dialogs[id] = pending;

        DialogRequested?.Invoke(dialog);
        RouteableInvocation.Current?.Asked();

        // No ConfigureAwait(false): the view model goes on where it asked, on the dispatcher of its session.
        try { return await pending.Answer.Task; }
        finally { _dialogs.TryRemove(id, out _); }
    }

    /// <summary>Gives the button <paramref name="index"/> as the answer of a dialog.</summary>
    /// <returns>Whether that dialog was waiting.</returns>
    public bool Answer(string id, int index)
    {
        if (id is null || !_dialogs.TryGetValue(id, out var pending))
            return false;

        if (index < 0 || (pending.Dialog.Buttons.Count > 0 && index >= pending.Dialog.Buttons.Count))
            return false;

        return pending.Answer.TrySetResult(index);
    }

    /// <summary>Whether a dialog with this id waits for an answer.</summary>
    public bool IsWaiting(string id) => id is not null && _dialogs.ContainsKey(id);

    /// <summary>Shows a notification.</summary>
    public void Notify(string title, string message, RouteableNotificationType type, TimeSpan? expiration, Action? onClick, Action? onClose)
    {
        var id = Interlocked.Increment(ref _nextToast);
        _toasts[id] = new Toast(onClick, onClose);
        ToastRequested?.Invoke(new RouteableToast(id, title, message, type.ToString(), expiration?.TotalSeconds));
    }

    /// <summary>The visitor closed a notification, or clicked it (then <paramref name="click"/> is true).</summary>
    public bool Dismiss(int id, bool click)
    {
        if (!_toasts.TryRemove(id, out var toast))
            return false;

        if (click)
            toast.OnClick?.Invoke();
        toast.OnClose?.Invoke();
        return true;
    }

    /// <summary>Shows <paramref name="viewModel"/> as the modal and completes with what it passes to <paramref name="subscribe"/>.</summary>
    public async Task<TResult?> ShowModalAsync<TResult>(object viewModel, Func<Action<TResult?>, IDisposable> subscribe)
    {
        ArgumentNullException.ThrowIfNull(subscribe);
        var closed = new TaskCompletionSource<TResult?>(TaskCreationOptions.RunContinuationsAsynchronously);

        // Subscribe first: a view model that closes right away must not be missed.
        using var subscription = subscribe(value => closed.TrySetResult(value));
        ModalOpened?.Invoke(viewModel);
        RouteableInvocation.Current?.Asked();
        try { return await closed.Task; }
        finally { ModalClosed?.Invoke(); }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        // A view model that awaits a dialog the visitor will never answer ends with the connection.
        foreach (var pending in _dialogs.Values)
            pending.Answer.TrySetCanceled();
        _dialogs.Clear();
        _toasts.Clear();
    }
}

/// <summary>
/// The call of a client that is running: what it does flows with it (an <see cref="AsyncLocal{T}"/>), so that a dialog it asks ends the wait of
/// that call, and not of another one.
/// </summary>
internal sealed class RouteableInvocation
{
    private static readonly AsyncLocal<RouteableInvocation?> CurrentInvocation = new();
    private readonly TaskCompletionSource _asked = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public static RouteableInvocation? Current
    {
        get => CurrentInvocation.Value;
        set => CurrentInvocation.Value = value;
    }

    /// <summary>Completes when the call asks the visitor something (a dialog, a modal).</summary>
    public Task AskedTask => _asked.Task;

    public void Asked() => _asked.TrySetResult();
}
