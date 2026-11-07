using Routeables.Services;

namespace Routeables.SignalR;

/// <summary>
/// The dialogs of a remote client: the dialog goes to the browser (<see cref="IRouteableClient.DialogRequested"/>) and the view model awaits
/// the button that is chosen, like it awaits a window on a desktop.
/// </summary>
public sealed class RemoteDialogService(RemoteInteractions interactions) : RouteableDialogServiceBase
{
    /// <inheritdoc />
    public override Task<int> ShowMessageAsync(string message, string title, string buttonText)
        => interactions.AskAsync("message", title, null, message, null, false, RouteableButtons.Split(buttonText));

    /// <inheritdoc />
    public override async Task<RouteableContentDialogResult> ShowContentDialogAsync(RouteableContentDialog @params)
    {
        ArgumentNullException.ThrowIfNull(@params);

        // The buttons that have a text, in the order of the other hosts: primary, secondary, close.
        var choices = new List<(string Text, RouteableContentDialogResult Result, Action? Click)>();
        if (!string.IsNullOrEmpty(@params.PrimaryButtonText) && @params.IsPrimaryButtonEnabled)
            choices.Add((@params.PrimaryButtonText, RouteableContentDialogResult.Primary, @params.PrimaryButtonClick));
        if (!string.IsNullOrEmpty(@params.SecondaryButtonText) && @params.IsSecondaryButtonEnabled)
            choices.Add((@params.SecondaryButtonText, RouteableContentDialogResult.Secondary, @params.SecondaryButtonClick));
        choices.Add((string.IsNullOrEmpty(@params.CloseButtonText) ? "Close" : @params.CloseButtonText, RouteableContentDialogResult.None, @params.CloseButtonClick));

        @params.Opening?.Invoke();
        var answer = interactions.AskAsync("content", @params.Title ?? string.Empty, null, @params.Content?.ToString(), null, false, [.. choices.Select(c => c.Text)]);
        @params.Opened?.Invoke();

        var chosen = choices[await answer];
        chosen.Click?.Invoke();
        @params.Closed?.Invoke();
        return chosen.Result;
    }

    /// <inheritdoc />
    public override async Task<RouteableTaskDialogResult> ShowTaskDialogAsync(RouteableTaskDialog @params, params RouteableTaskDialogResult[] results)
    {
        ArgumentNullException.ThrowIfNull(@params);
        RouteableTaskDialogResult[] buttons = results is { Length: > 0 } ? results : [RouteableTaskDialogResult.OK];

        @params.Opening?.Invoke();
        var answer = interactions.AskAsync(
            "task", @params.Title ?? string.Empty, @params.Header, @params.Content?.ToString(),
            @params.ShowsFooter ? @params.Footer?.ToString() : null, @params.ShowProgressBar, [.. buttons.Select(b => b.ToString())]);
        @params.Opened?.Invoke();

        var result = buttons[await answer];
        @params.Closed?.Invoke();
        return result;
    }
}
