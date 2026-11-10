using System.ComponentModel;
using Routeables.Abstraction;
using Routeables.SignalR;
using IRouter = Routeables.Abstraction.Routing.IRouter;
using XAnimals.ViewModels;
using Routeables.Abstraction.Routing;

namespace XAnimals.Server;

/// <summary>
/// The pets of the Adopt a pet page, watched as <c>$pets</c>: every page of the history of the region "adopt", up to the current one.
/// The page itself shows one status line; a front end that draws the cards side by side asks for the whole history.
/// </summary>
[RouteableState]
internal sealed class PetsViewModel : INotifyPropertyChanged, IDisposable
{
    private readonly IRouter _region;
    private IReadOnlyList<PetViewModel> _pets;

    public PetsViewModel(IServiceProvider provider)
    {
        _region = provider.GetRouter(AdoptViewModel.Region);
        _pets = Read();
        _region.History.Changed += OnHistoryChanged;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>The adopted animals, in the order they were adopted.</summary>
    public IReadOnlyList<PetViewModel> Pets
    {
        get => _pets;
        private set
        {
            _pets = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Pets)));
        }
    }

    private List<PetViewModel> Read()
        => [.. _region.History.Entries.Take(_region.History.CurrentIndex + 1).Select(e => e.ViewModel).OfType<PetViewModel>()];

    private void OnHistoryChanged(object? sender, EventArgs e) => Pets = Read();

    public void Dispose() => _region.History.Changed -= OnHistoryChanged;
}
