using System.Collections.Concurrent;
using Routeables.Services;

namespace Routeables.SignalR;

/// <summary>
/// What the dialogs, the notifications and the modals of a connection share with its session. On a desktop a dialog is a window and the
/// view model awaits it; here the view model awaits too, but the dialog is sent to the browser and the answer comes back by the hub
/// (<c>Answer</c>). One per connection (scoped).
/// </summary>
public sealed class RemoteInteractions : IDisposable
{
    private sealed record Pending(RouteableDialog Dialog, TaskCompletionSource<int> Answer);

    private sealed record Toast(Action? OnClick, Action? OnClose);

    private readonly ConcurrentDictionary<string, Pending> _dialogs = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<int, Toast> _toasts = new();
    private TaskCompletionSource _asked = new(TaskCreationOptions.RunContinuationsAsynchronously);
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

    /// <summary>Shows a dialog and completes with the index of the button that was chosen.</summary>
    public async Task<int> AskAsync(string kind, string title, string? header, string? body, string? footer, bool showProgress, IReadOnlyList<string> buttons)
    {
        var id = "d" + Interlocked.Increment(ref _nextDialog).ToString(System.Globalization.CultureInfo.InvariantCulture);
        var dialog = new RouteableDialog(id, kind, title, header, body, footer, showProgress, buttons);
        var pending = new Pending(dialog, new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously));
        _dialogs[id] = pending;

        DialogRequested?.Invoke(dialog);
        _asked.TrySetResult();
        try { return await pending.Answer.Task.ConfigureAwait(false); }
        finally { _dialogs.TryRemove(id, out _); }
    }

    /// <summary>Gives the button <paramref name="index"/> as the answer of a dialog.</summary>
    /// <returns>Whether that dialog was waiting.</returns>
    public bool Answer(string id, int index)
    {
        if (!_dialogs.TryGetValue(id, out var pending))
            return false;

        if (index < 0 || (pending.Dialog.Buttons.Count > 0 && index >= pending.Dialog.Buttons.Count))
            return false;

        return pending.Answer.TrySetResult(index);
    }

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
        var closed = new TaskCompletionSource<TResult?>(TaskCreationOptions.RunContinuationsAsynchronously);

        // Subscribe first: a view model that closes right away must not be missed.
        using var subscription = subscribe(value => closed.TrySetResult(value));
        ModalOpened?.Invoke(viewModel);
        _asked.TrySetResult();
        try { return await closed.Task.ConfigureAwait(false); }
        finally { ModalClosed?.Invoke(); }
    }

    /// <summary>
    /// A task that completes the next time something asks the visitor (a dialog or a modal opens). A call that starts a command waits
    /// for the command to end or for this, as a command that opens a dialog does not end before the visitor answered.
    /// </summary>
    internal Task Arm()
    {
        _asked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        return _asked.Task;
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
