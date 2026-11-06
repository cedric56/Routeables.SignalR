using Routeables.Services;

namespace Routeables.SignalR;

/// <summary>The notifications of a remote client: they go to the browser (<see cref="IRouteableClient.Notified"/>), which closes them after their time.</summary>
public sealed class RemoteNotificationService(RemoteInteractions interactions) : IRouteableNotificationService
{
    /// <inheritdoc />
    public void Show(string title, string message, RouteableNotificationType type = RouteableNotificationType.Information, TimeSpan? expiration = null, Action? onClick = null, Action? onClose = null)
        => interactions.Notify(title, message, type, expiration, onClick, onClose);
}
