using System.Collections;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows.Input;
using Routeables.Abstraction;

namespace Routeables.SignalR;

/// <summary>
/// Turns a view model into what a client can hold: a dictionary of its public properties, and the list of its commands.
/// A value is a scalar, a list, or a dictionary of the same kind; what cannot be sent (delegates, tasks, types, the services
/// a view model holds) is left out, and so is a property marked <see cref="JsonIgnoreAttribute"/>. A command (<see cref="ICommand"/>)
/// is sent as <c>{ "$command": path, "canExecute": bool }</c>: the client gives the path back to run it, wherever the command is
/// (<c>adopt</c>, <c>choices.2.adoptCommand</c>).
/// </summary>
/// <remarks>It reads the types by reflection: it is not meant for trimmed or ahead-of-time compiled applications.</remarks>
public static class RouteableStateProjector
{
    private const int MaxDepth = 4;

    /// <summary>The camel case name a client uses for a property or a command.</summary>
    public static string ClientName(string name)
        => string.IsNullOrEmpty(name) ? name : JsonNamingPolicy.CamelCase.ConvertName(name);

    /// <summary>The state of <paramref name="viewModel"/>.</summary>
    public static IReadOnlyDictionary<string, object?> Project(object viewModel)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        return ProjectObject(viewModel, 0, new HashSet<object>(ReferenceEqualityComparer.Instance), string.Empty);
    }

    /// <summary>The value of one property of a view model, as a client holds it.</summary>
    /// <param name="value">The value of the property.</param>
    /// <param name="path">The client name of the property: the path of the commands inside the value starts with it.</param>
    public static object? ProjectValue(object value, string path = "")
        => ProjectValue(value, 1, new HashSet<object>(ReferenceEqualityComparer.Instance), path);

    /// <summary>
    /// What can tell that the state of a view model changed: the view model itself, and every object of its state that raises a change
    /// (<see cref="INotifyPropertyChanged"/>, <see cref="INotifyCollectionChanged"/>, the <see cref="ICommand.CanExecuteChanged"/> of a command).
    /// </summary>
    /// <returns>Each source with the client name of the property of the view model that holds it; null for the view model itself.</returns>
    public static IReadOnlyList<(object Source, string? Property)> Sources(object viewModel)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        var sources = new List<(object, string?)>();
        var visited = new HashSet<object>(ReferenceEqualityComparer.Instance) { viewModel };
        if (viewModel is INotifyPropertyChanged)
            sources.Add((viewModel, null));

        foreach (var property in StateProperties(viewModel.GetType()))
        {
            object? value;
            try { value = property.GetValue(viewModel); }
            catch (TargetInvocationException) { continue; }

            if (value is not null)
                Collect(value, ClientName(property.Name), 1, visited, sources);
        }
        return sources;
    }

    /// <summary>Whether the property is part of the state of its view model.</summary>
    public static bool IsState(PropertyInfo property)
        => property.GetMethod is { IsPublic: true, IsStatic: false }
           && property.GetIndexParameters().Length == 0
           && property.GetCustomAttribute<JsonIgnoreAttribute>() is null
           && IsSendable(property.PropertyType);

    /// <summary>The public instance properties of a type that are part of its state.</summary>
    public static IEnumerable<PropertyInfo> StateProperties(Type type)
        => type.GetProperties(BindingFlags.Public | BindingFlags.Instance).Where(IsState);

    /// <summary>The methods of a type that a client may call, by their client name.</summary>
    public static IReadOnlyDictionary<string, MethodInfo> Commands(Type type)
    {
        var commands = new Dictionary<string, MethodInfo>(StringComparer.Ordinal);
        foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.Instance))
        {
            var attribute = method.GetCustomAttribute<RouteableCommandAttribute>(inherit: true);
            if (attribute is null || method.IsGenericMethodDefinition || method.IsSpecialName)
                continue;

            commands[ClientName(attribute.Name ?? method.Name)] = method;
        }
        return commands;
    }

    private static Dictionary<string, object?> ProjectObject(object value, int depth, HashSet<object> seen, string path)
    {
        var state = new Dictionary<string, object?>(StringComparer.Ordinal);
        if (!seen.Add(value))
            return state;

        try
        {
            foreach (var property in StateProperties(value.GetType()))
            {
                object? propertyValue;
                try { propertyValue = property.GetValue(value); }
                catch (TargetInvocationException) { continue; }

                var name = ClientName(property.Name);
                state[name] = propertyValue is null ? null : ProjectValue(propertyValue, depth + 1, seen, Join(path, name));
            }
        }
        finally
        {
            seen.Remove(value);
        }
        return state;
    }

    private static string Join(string path, string name) => path.Length == 0 ? name : path + "." + name;

    private static object? ProjectValue(object value, int depth, HashSet<object> seen, string path)
    {
        var type = value.GetType();
        if (IsScalar(type))
            return value is Enum || FromString(type) is not null ? value.ToString() : value;

        if (value is ICommand command)
            return new Dictionary<string, object?>(StringComparer.Ordinal) { ["$command"] = path, ["canExecute"] = SafeCanExecute(command) };

        if (depth > MaxDepth)
            return null;

        if (value is IDictionary dictionary)
        {
            var map = new Dictionary<string, object?>(StringComparer.Ordinal);
            foreach (DictionaryEntry entry in dictionary)
            {
                var key = Convert.ToString(entry.Key) ?? string.Empty;
                map[key] = entry.Value is null ? null : ProjectValue(entry.Value, depth + 1, seen, Join(path, key));
            }
            return map;
        }

        if (value is IEnumerable sequence)
        {
            var list = new List<object?>();
            foreach (var item in sequence)
                list.Add(item is null ? null : ProjectValue(item, depth + 1, seen, Join(path, list.Count.ToString(System.Globalization.CultureInfo.InvariantCulture))));
            return list;
        }

        return ProjectObject(value, depth, seen, path);
    }

    private static bool SafeCanExecute(ICommand command)
    {
        try { return command.CanExecute(null); }
        catch (Exception) { return false; }
    }

    private static void Collect(object value, string property, int depth, HashSet<object> visited, List<(object, string?)> sources)
    {
        var type = value.GetType();
        if (IsScalar(type) || depth > MaxDepth || !visited.Add(value))
            return;

        if (value is INotifyPropertyChanged or INotifyCollectionChanged or ICommand)
            sources.Add((value, property));

        if (value is ICommand)
            return;

        if (value is IDictionary dictionary)
        {
            foreach (DictionaryEntry entry in dictionary)
                if (entry.Value is not null)
                    Collect(entry.Value, property, depth + 1, visited, sources);
            return;
        }

        if (value is IEnumerable sequence)
        {
            foreach (var item in sequence)
                if (item is not null)
                    Collect(item, property, depth + 1, visited, sources);
            return;
        }

        foreach (var child in StateProperties(type))
        {
            object? childValue;
            try { childValue = child.GetValue(value); }
            catch (TargetInvocationException) { continue; }

            if (childValue is not null)
                Collect(childValue, property, depth + 1, visited, sources);
        }
    }

    /// <summary>
    /// The conversion from a string of a type that is a name (<c>RouteTransition</c>: <c>RouteTransition t = "Fade"</c>), or
    /// <see langword="null"/>. Such a value is sent as its text, and a client sets it with a text.
    /// </summary>
    public static MethodInfo? FromString(Type type)
    {
        type = Nullable.GetUnderlyingType(type) ?? type;
        if (type == typeof(string) || type.IsPrimitive || type.IsEnum)
            return null;

        return type.GetMethod("op_Implicit", BindingFlags.Public | BindingFlags.Static, [typeof(string)]) is { } method && method.ReturnType == type
            ? method
            : null;
    }

    private static bool IsScalar(Type type)
    {
        type = Nullable.GetUnderlyingType(type) ?? type;
        return type.IsPrimitive || type.IsEnum
            || FromString(type) is not null
            || type == typeof(string) || type == typeof(decimal) || type == typeof(Guid)
            || type == typeof(DateTime) || type == typeof(DateTimeOffset) || type == typeof(TimeSpan)
            || type == typeof(DateOnly) || type == typeof(TimeOnly) || type == typeof(Uri);
    }

    // What a client can hold. A type is refused when it is a behaviour (delegate, command, task) or a service the view model uses.
    private static bool IsSendable(Type type)
    {
        type = Nullable.GetUnderlyingType(type) ?? type;
        if (typeof(Delegate).IsAssignableFrom(type) || typeof(Task).IsAssignableFrom(type)
            || typeof(Type).IsAssignableFrom(type) || typeof(Stream).IsAssignableFrom(type)
            || typeof(IServiceProvider).IsAssignableFrom(type) || typeof(IRouter).IsAssignableFrom(type))
            return false;

        return !type.IsPointer && !type.IsByRef && !type.IsGenericTypeDefinition;
    }
}
