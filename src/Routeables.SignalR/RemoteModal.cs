using Routeables.Abstraction;
using Routeables.Abstraction.Bases;

namespace Routeables.SignalR;

/// <summary>
/// The "view" of a modal view model for a remote client: it sends the view model to the browser, as a page of the target
/// <see cref="RouteableTargets.Modal"/>, and returns what the view model passes to <see cref="ICloseableViewModel{TResult}.CloseAsync"/>.
/// Derive from it (an empty class) and mark it with <c>[RouteableView(typeof(TViewModel))]</c>.
/// </summary>
/// <typeparam name="TViewModel">The view model of the modal.</typeparam>
/// <typeparam name="TResult">What the modal returns.</typeparam>
public abstract class RemoteModal<TViewModel, TResult>(RemoteInteractions interactions) : IRouteableModalFor<TViewModel, TResult>
    where TViewModel : class, ICloseableViewModel<TResult>
{
    /// <inheritdoc />
    public object? Context { get; set; }

    /// <inheritdoc />
    public Task<TResult?> ShowModalAsync()
    {
        var viewModel = (TViewModel)(Context ?? throw new InvalidOperationException("The modal has no view model."));
        return interactions.ShowModalAsync<TResult>(viewModel, close =>
        {
            void OnClose(object? sender, TResult? value) => close(value);
            viewModel.CloseRequested += OnClose;
            return new Unsubscribe(() => viewModel.CloseRequested -= OnClose);
        });
    }

    private sealed class Unsubscribe(Action action) : IDisposable
    {
        public void Dispose() => action();
    }
}
