using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Input;
using Routeables.Abstraction;
using Routeables.Services;
using XAnimals.Common;

namespace XAnimals.AngularServer;

/// <summary>An entry of the menu, as the app draws it.</summary>
/// <param name="Name">What the entry is called.</param>
/// <param name="Route">The address the app goes to.</param>
/// <param name="Glyph">Its icon.</param>
/// <param name="Description">What the page shows, in one line.</param>
/// <param name="IsLocked">Whether the entry cannot be followed now (a page published a lock).</param>
internal sealed record MenuEntry(string Name, string Route, string Glyph, string Description, bool IsLocked);

/// <summary>
/// What the app shows around the pages, watched as <c>$shell</c>: the menu, built from the main view model (it follows its lock),
/// and the theme. The app goes to the route of an entry itself.
/// </summary>
internal sealed class ShellViewModel : INotifyPropertyChanged, IDisposable
{
    // The Birds module has views for the other platforms only.
    private static readonly string[] Unserved = ["/birds"];

    private readonly IMainViewModel _main;
    private readonly ClientTheme _theme;

    public ShellViewModel(IMainViewModel main, ClientTheme theme)
    {
        _main = main;
        _theme = theme;
        _main.LockChanged += OnLockChanged;
        _theme.Changed += OnThemeChanged;
        ThemeCommand = new Command(NextTheme);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>The entries of the menu.</summary>
    public IReadOnlyList<MenuEntry> Entries => [.. _main.Routeables
        .Select(e => (Entry: e, Route: "/" + e.Route.TrimStart('/')))
        .Where(e => !Unserved.Contains(e.Route, StringComparer.OrdinalIgnoreCase))
        .Select(e => new MenuEntry(
            e.Entry.DisplayName, e.Route, AnimalIcons.GlyphOf(e.Entry.Path ?? string.Empty),
            e.Entry.Description?.Split('\n')[0].Trim() ?? string.Empty, e.Entry.IsLock))];

    /// <summary>Whether a page published a lock.</summary>
    public bool IsLocked => _main.Routeables.Any(e => e.IsLock);

    /// <summary>The theme that was asked for: Default (the browser decides), Light or Dark.</summary>
    public string Theme => _theme.Requested.ToString();

    /// <summary>Default, then light, then dark.</summary>
    public ICommand ThemeCommand { get; }

    private void NextTheme() => _theme.Request(_theme.Requested switch
    {
        RouteableRequestedTheme.Default => RouteableRequestedTheme.Light,
        RouteableRequestedTheme.Light => RouteableRequestedTheme.Dark,
        _ => RouteableRequestedTheme.Default
    });

    private void OnLockChanged(object? sender, EventArgs e)
    {
        Raise(nameof(Entries));
        Raise(nameof(IsLocked));
    }

    private void OnThemeChanged(object? sender, EventArgs e) => Raise(nameof(Theme));

    private void Raise(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    public void Dispose()
    {
        _main.LockChanged -= OnLockChanged;
        _theme.Changed -= OnThemeChanged;
    }

    private sealed class Command(Action run) : ICommand
    {
        public event EventHandler? CanExecuteChanged { add { } remove { } }

        public bool CanExecute(object? parameter) => true;

        public void Execute(object? parameter) => run();
    }
}
