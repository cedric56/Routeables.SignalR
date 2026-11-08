using System.Collections;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Reflection;
using System.Windows.Input;

namespace Routeables.SignalR;

/// <summary>Something of a state that could not be sent as it is.</summary>
/// <param name="Property">The client name of the property of the view model.</param>
/// <param name="Message">Why.</param>
/// <param name="Code">What kind of problem (<see cref="RouteableErrorCodes"/>).</param>
/// <param name="Retry">The state changed while it was read (from another thread): reading it again may succeed.</param>
/// <param name="Exception">
/// What was thrown while the property was read, if anything. Its message is in <paramref name="Message"/>: the hub sends to the client a message
/// without it, unless detailed errors are on (<see cref="RouteablesOptions.DetailedErrors"/>), and logs the exception.
/// </param>
public sealed record RouteableProjectionIssue(string Property, string Message, string Code, bool Retry = false, Exception? Exception = null);

/// <summary>
/// Whether the user may read a property of an object inside a state (not of the view model itself, which is filtered before): the client name of
/// the property of the view model that holds the object, the object, its shape and its property. It is asked during the projection, which does not wait.
/// </summary>
internal delegate bool ReadGuard(string property, object owner, TypeShape shape, StateMember member);

/// <summary>
/// Turns a view model into what a client can hold: a dictionary of the properties the schema exposes, and the list of its commands.
/// A value is a scalar, a list, or a dictionary of the same kind; what cannot be sent (delegates, tasks, types, the services a view model holds)
/// is left out. A command (<see cref="ICommand"/>) is sent as <c>{ "$command": path, "canExecute": bool }</c>: the client gives the path back to run
/// it, wherever the command is (<c>adopt</c>, <c>choices.2.adoptCommand</c>). The limits of <see cref="RouteablesOptions"/> apply to each property.
/// </summary>
/// <remarks>It reads the types by reflection: it is not meant for trimmed or ahead-of-time compiled applications.</remarks>
public sealed class RouteableStateProjector
{
    private readonly RouteableSchema _schema;

    /// <summary>Creates the projector of a schema.</summary>
    public RouteableStateProjector(RouteableSchema schema)
    {
        _schema = schema ?? throw new ArgumentNullException(nameof(schema));
    }

    /// <summary>The schema it follows.</summary>
    public RouteableSchema Schema => _schema;

    private RouteablesOptions Options => _schema.Options;

    /// <summary>The state of <paramref name="viewModel"/>.</summary>
    /// <param name="viewModel">The view model.</param>
    /// <param name="issues">Told of what could not be sent; the value is then <see langword="null"/>.</param>
    /// <param name="visible">Which properties of the view model the user may see; all when <see langword="null"/>.</param>
    /// <remarks>
    /// The authorization of the properties of the objects inside the state (<c>[Authorize]</c>, <c>[RouteableAuthorize]</c> on a nested type or
    /// one of its properties) is not applied here: the hub applies it to what it sends.
    /// </remarks>
    public IReadOnlyDictionary<string, object?> Project(object viewModel, Action<RouteableProjectionIssue>? issues = null, Func<PropertyInfo, bool>? visible = null)
        => Project(viewModel, issues, visible, guard: null);

    internal IReadOnlyDictionary<string, object?> Project(object viewModel, Action<RouteableProjectionIssue>? issues, Func<PropertyInfo, bool>? visible, ReadGuard? guard)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        var state = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var member in _schema.Shape(viewModel.GetType()).State)
        {
            if (visible is not null && !visible(member.Property))
                continue;
            if (TryRead(viewModel, member.Property, out var value, out var failure))
                state[member.Name] = ProjectAt(member.Name, member.Name, value, depth: 1, issues, guard);
            else
                issues?.Invoke(ReadFailure(member.Name, failure!));
        }
        return state;
    }

    /// <summary>The value of one property of a view model, as a client holds it, within the limits of one property.</summary>
    /// <param name="property">The client name of the property: the path of the commands inside the value starts with it.</param>
    /// <param name="value">The value of the property.</param>
    /// <param name="issues">Told of what could not be sent.</param>
    public object? ProjectProperty(string property, object? value, Action<RouteableProjectionIssue>? issues = null)
        => ProjectAt(property, property, value, depth: 1, issues);

    /// <summary>A value at <paramref name="path"/> inside the property <paramref name="property"/> (an item of a list), as a client holds it.</summary>
    internal object? ProjectAt(string property, string path, object? value, int depth, Action<RouteableProjectionIssue>? issues, ReadGuard? guard = null)
    {
        if (value is null)
            return null;

        var budget = new Budget(Options.MaxStateNodes);
        try
        {
            return ProjectValue(value, depth, new HashSet<object>(ReferenceEqualityComparer.Instance), path, budget, property, guard);
        }
        catch (LimitException limit)
        {
            issues?.Invoke(new RouteableProjectionIssue(property, $"'{property}' is not sent: {limit.Message}", RouteableErrorCodes.Limit));
            return null;
        }
        catch (InvalidOperationException exception)
        {
            // A collection changed while it was read: the change that did it is told too.
            issues?.Invoke(new RouteableProjectionIssue(property, $"'{property}' changed while it was read: {exception.Message}", RouteableErrorCodes.Failed, Retry: true, exception));
            return null;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            if (exception is TargetInvocationException { InnerException: { } inner })
                exception = inner;
            issues?.Invoke(new RouteableProjectionIssue(property, $"'{property}' could not be read: {exception.Message}", RouteableErrorCodes.Failed, Exception: exception));
            return null;
        }
    }

    /// <summary>The commands of the type of <paramref name="viewModel"/>, by their client name.</summary>
    public IReadOnlyList<string> CommandNames(object viewModel)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        return [.. _schema.Shape(viewModel.GetType()).Commands.Keys];
    }

    /// <summary>
    /// What can tell that the state of a view model changed: the view model itself, and every object of its state that raises a change
    /// (<see cref="INotifyPropertyChanged"/>, <see cref="INotifyCollectionChanged"/>, the <see cref="ICommand.CanExecuteChanged"/> of a command).
    /// </summary>
    /// <returns>Each source with the client name of the property of the view model that holds it; null for the view model itself.</returns>
    public IReadOnlyList<(object Source, string? Property)> Sources(object viewModel)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        var sources = new List<(object, string?)>();
        if (viewModel is INotifyPropertyChanged)
            sources.Add((viewModel, null));

        foreach (var member in _schema.Shape(viewModel.GetType()).State)
            sources.AddRange(SourcesOf(viewModel, member));
        return sources;
    }

    /// <summary>The sources inside one property of a view model.</summary>
    internal IReadOnlyList<(object Source, string? Property)> SourcesOf(object viewModel, StateMember member)
    {
        var sources = new List<(object, string?)>();
        if (!TryRead(viewModel, member.Property, out var value) || value is null)
            return sources;

        var visited = new HashSet<object>(ReferenceEqualityComparer.Instance) { viewModel };
        try { Collect(value, member.Name, 1, visited, sources); }
        catch (InvalidOperationException) { }   // a collection that changed while it was read: the change is told, and read again
        return sources;
    }

    internal static bool TryRead(object instance, PropertyInfo property, out object? value) => TryRead(instance, property, out value, out _);

    internal static bool TryRead(object instance, PropertyInfo property, out object? value, out Exception? failure)
    {
        try
        {
            value = property.GetValue(instance);
            failure = null;
            return true;
        }
        catch (TargetInvocationException exception)
        {
            value = null;
            failure = exception.InnerException ?? exception;
            return false;
        }
    }

    /// <summary>A getter of the view model threw: the property is not sent, and the client is told (the hub hides the message, see the issue).</summary>
    internal static RouteableProjectionIssue ReadFailure(string property, Exception exception)
        => new(property, $"'{property}' could not be read: {exception.Message}", RouteableErrorCodes.Failed, Exception: exception);

    private static string Join(string path, string name) => path.Length == 0 ? name : path + "." + name;

    private object? ProjectValue(object value, int depth, HashSet<object> seen, string path, Budget budget, string property, ReadGuard? guard)
    {
        budget.Spend();
        var type = value.GetType();
        if (RouteableSchema.IsScalar(type))
            return value is Enum || RouteableSchema.FromString(type) is not null ? value.ToString() : value;

        if (value is ICommand command)
            return new Dictionary<string, object?>(StringComparer.Ordinal) { ["$command"] = path, ["canExecute"] = SafeCanExecute(command) };

        if (depth > Options.MaxDepth || !RouteableSchema.IsSendable(type))
            return null;

        if (!seen.Add(value))
            return null;   // a cycle
        try
        {
            if (value is IDictionary dictionary)
            {
                CheckCount(dictionary.Count);
                var map = new Dictionary<string, object?>(StringComparer.Ordinal);
                foreach (DictionaryEntry entry in dictionary)
                {
                    var key = Convert.ToString(entry.Key, System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty;
                    map[key] = entry.Value is null ? null : ProjectValue(entry.Value, depth + 1, seen, Join(path, key), budget, property, guard);
                }
                return map;
            }

            if (value is IEnumerable sequence)
            {
                if (value is ICollection collection)
                    CheckCount(collection.Count);
                var list = new List<object?>();
                foreach (var item in sequence)
                {
                    if (list.Count >= Options.MaxCollectionItems)
                        throw new LimitException($"it holds more than {Options.MaxCollectionItems} items (RouteablesOptions.MaxCollectionItems): page it");
                    list.Add(item is null ? null : ProjectValue(item, depth + 1, seen, Join(path, list.Count.ToString(System.Globalization.CultureInfo.InvariantCulture)), budget, property, guard));
                }
                return list;
            }

            var state = new Dictionary<string, object?>(StringComparer.Ordinal);
            var shape = _schema.Shape(type);
            foreach (var member in shape.State)
            {
                if (guard is not null && !guard(property, value, shape, member))
                    continue;   // the user may not read it: it is not sent at all
                if (!TryRead(value, member.Property, out var propertyValue))
                    continue;
                state[member.Name] = propertyValue is null ? null : ProjectValue(propertyValue, depth + 1, seen, Join(path, member.Name), budget, property, guard);
            }
            return state;
        }
        finally
        {
            seen.Remove(value);
        }
    }

    private void CheckCount(int count)
    {
        if (count > Options.MaxCollectionItems)
            throw new LimitException($"it holds {count} items, more than {Options.MaxCollectionItems} (RouteablesOptions.MaxCollectionItems): page it");
    }

    private static bool SafeCanExecute(ICommand command)
    {
        try { return command.CanExecute(null); }
        catch (Exception) { return false; }
    }

    private void Collect(object value, string property, int depth, HashSet<object> visited, List<(object, string?)> sources)
    {
        var type = value.GetType();
        if (RouteableSchema.IsScalar(type) || depth > Options.MaxDepth || !visited.Add(value))
            return;

        if (value is INotifyPropertyChanged or INotifyCollectionChanged or ICommand)
            sources.Add((value, property));

        if (value is ICommand)
            return;

        if (value is IDictionary dictionary)
        {
            if (dictionary.Count > Options.MaxCollectionItems)
                return;
            foreach (DictionaryEntry entry in dictionary)
                if (entry.Value is not null)
                    Collect(entry.Value, property, depth + 1, visited, sources);
            return;
        }

        if (value is IEnumerable sequence)
        {
            if (value is ICollection { Count: var count } && count > Options.MaxCollectionItems)
                return;
            var seen = 0;
            foreach (var item in sequence)
            {
                if (++seen > Options.MaxCollectionItems)
                    return;
                if (item is not null)
                    Collect(item, property, depth + 1, visited, sources);
            }
            return;
        }

        foreach (var member in _schema.Shape(type).State)
        {
            if (TryRead(value, member.Property, out var child) && child is not null)
                Collect(child, property, depth + 1, visited, sources);
        }
    }

    private sealed class Budget(int nodes)
    {
        private readonly int _nodes = nodes;
        private int _left = nodes;

        public void Spend()
        {
            if (--_left < 0)
                throw new LimitException($"it holds more than {_nodes} values (RouteablesOptions.MaxStateNodes)");
        }
    }

    private sealed class LimitException(string message) : Exception(message);
}
