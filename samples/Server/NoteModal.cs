using Routeables.Abstraction;
using Routeables.SignalR;
using XAnimals.ViewModels;

namespace XAnimals.Server;

/// <summary>
/// The view of <see cref="NoteViewModel"/> for the Angular app: the view model goes to the browser as the page of the target
/// <c>$modal</c>, and the note comes back when the view model asks to close.
/// </summary>
[RouteableView(typeof(NoteViewModel))]
internal sealed class NoteModal(RemoteInteractions interactions) : RemoteModal<NoteViewModel, string?>(interactions);
