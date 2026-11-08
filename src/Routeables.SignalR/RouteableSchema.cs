using System.Collections;
using System.Collections.Concurrent;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows.Input;
using Microsoft.AspNetCore.Authorization;
using Routeables.Abstraction;

namespace Routeables.SignalR;

/// <summary>
/// What the client may see and do of each view model type, read once by reflection and kept: the properties that are sent, those it may set,
/// the commands it may call, and the authorization each of them asks for. One per application (<see cref="RouteablesOptions"/>).
/// </summary>
/// <remarks>It reads the types by reflection: it is not meant for trimmed or ahead-of-time compiled applications.</remarks>
public sealed class RouteableSchema
{
    private readonly ConcurrentDictionary<Type, TypeShape> _shapes = new();
    private readonly ConcurrentDictionary<Type, bool> _mayHoldCommands = new();

    /// <summary>Creates the schema of the given options.</summary>
    public RouteableSchema(RouteablesOptions options)
    {
        Options = options ?? throw new ArgumentNullException(nameof(options));
    }

    /// <summary>The options it follows.</summary>
    public RouteablesOptions Options { get; }

    /// <summary>The camel case name a client uses for a property or a command.</summary>
    public static string ClientName(string name)
        => string.IsNullOrEmpty(name) ? name : JsonNamingPolicy.CamelCase.ConvertName(name);

    /// <summary>The properties of <paramref name="type"/> that are sent to the client.</summary>
    public IEnumerable<PropertyInfo> StateProperties(Type type) => Shape(type).State.Select(m => m.Property);

    /// <summary>The properties of <paramref name="type"/> that the client may set.</summary>
    public IEnumerable<PropertyInfo> SettableProperties(Type type) => Shape(type).State.Where(m => m.Settable).Select(m => m.Property);

    /// <summary>The methods of a type that a client may call, by their client name.</summary>
    public IReadOnlyDictionary<string, MethodInfo> Commands(Type type)
        => Shape(type).Commands.ToDictionary(c => c.Key, c => c.Value.Method, StringComparer.Ordinal);

    internal TypeShape Shape(Type type) => _shapes.GetOrAdd(type, static (t, schema) => schema.Read(t), this);

    /// <summary>Whether a value of this type may hold a command once projected: its items can then not be moved without being sent again.</summary>
    internal bool MayHoldCommands(Type type) => MayHoldCommands(type, 0);

    private bool MayHoldCommands(Type type, int depth)
    {
        if (_mayHoldCommands.TryGetValue(type, out var known))
            return known;
        if (depth > Options.MaxDepth + 1)
            return false;

        bool result;
        if (typeof(ICommand).IsAssignableFrom(type))
            result = true;
        else if (IsScalar(type))
            result = false;
        else if (type == typeof(object) || type.IsInterface && !typeof(IEnumerable).IsAssignableFrom(type) || type.IsAbstract)
            result = true;   // unknown at this point
        else if (typeof(IEnumerable).IsAssignableFrom(type))
            result = ItemType(type) is not { } item || MayHoldCommands(item, depth + 1);
        else
        {
            _mayHoldCommands[type] = false;   // a type that holds itself
            result = Shape(type).State.Any(m => MayHoldCommands(m.Property.PropertyType, depth + 1));
        }
        _mayHoldCommands[type] = result;
        return result;
    }

    private static Type? ItemType(Type type)
    {
        if (type.IsArray)
            return type.GetElementType();
        var dictionary = type.GetInterfaces().Append(type)
            .FirstOrDefault(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IDictionary<,>));
        if (dictionary is not null)
            return dictionary.GetGenericArguments()[1];
        var enumerable = type.GetInterfaces().Append(type)
            .FirstOrDefault(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IEnumerable<>));
        return enumerable?.GetGenericArguments()[0];
    }

    private TypeShape Read(Type type)
    {
        var classState = type.GetCustomAttribute<RouteableStateAttribute>(inherit: true);
        var (exposedType, exposedSettable) = Options.ExposedType(type);

        // The buttons a view model of Routeables offers (a name and a command) are made to be shown: read-only.
        exposedType |= typeof(RouteableCommand).IsAssignableFrom(type);
        var classAuthorize = type.GetCustomAttributes(inherit: true).OfType<IAuthorizeData>().ToArray();
        var classAnonymous = type.GetCustomAttributes(inherit: true).OfType<IAllowAnonymous>().Any();

        var state = new List<StateMember>();
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (!IsSendableProperty(property))
                continue;

            var marked = Attribute.GetCustomAttribute(property, typeof(RouteableStateAttribute), inherit: true) as RouteableStateAttribute;
            bool readable, settable;
            if (Options.Exposure == RouteableExposure.Public)
            {
                readable = true;
                settable = true;
            }
            else
            {
                readable = marked is not null || classState is not null || exposedType;
                // A property that says whether it may be set wins over its class and over ExposeTypes.
                settable = marked is { SettableIsSet: true } ? marked.Settable : classState?.Settable == true || exposedSettable;
            }
            if (!readable)
                continue;

            var name = ClientName(property.Name);
            if (!names.Add(name))
                continue;   // a property hidden by "new": the most derived one is listed first

            settable &= property.SetMethod is { IsPublic: true, IsStatic: false } && !typeof(ICommand).IsAssignableFrom(property.PropertyType);
            state.Add(new StateMember(
                property, name, settable,
                Attribute.GetCustomAttributes(property, inherit: true).OfType<IAuthorizeData>().ToArray(),
                Attribute.GetCustomAttributes(property, inherit: true).OfType<IAllowAnonymous>().Any()));
        }

        var commands = new Dictionary<string, CommandMember>(StringComparer.Ordinal);
        foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.Instance))
        {
            var attribute = method.GetCustomAttribute<RouteableCommandAttribute>(inherit: true);
            if (attribute is null || method.IsGenericMethodDefinition || method.IsSpecialName)
                continue;
            if (method.GetParameters().Any(p => p.ParameterType.IsByRef || p.ParameterType.IsPointer))
                continue;

            var command = new CommandMember(
                method, method.GetParameters(), ReturnKindOf(method.ReturnType),
                method.GetCustomAttributes(inherit: true).OfType<IAuthorizeData>().ToArray(),
                method.GetCustomAttributes(inherit: true).OfType<IAllowAnonymous>().Any());
            var commandName = ClientName(attribute.Name ?? method.Name);
            if (commands.TryGetValue(commandName, out var other))
            {
                // A method hidden by "new" in a derived class: the most derived one is the command, as for a property.
                if (Hides(method, other.Method))
                    commands[commandName] = command;
                else if (!Hides(other.Method, method))
                    throw new InvalidOperationException(
                        $"{type.FullName} has two commands named '{commandName}' ({other.Method.DeclaringType?.Name}.{other.Method.Name} and " +
                        $"{method.DeclaringType?.Name}.{method.Name}): which one a client calls must not depend on the order of reflection. Name them apart (RouteableCommand(Name = ...)).");
                continue;
            }
            commands[commandName] = command;
        }

        return new TypeShape(type, [.. state], state.ToDictionary(m => m.Name, StringComparer.Ordinal), commands, classAuthorize, classAnonymous);
    }

    // Whether "derived" hides "hidden" with "new": same name, same parameters, declared in a class that derives from the other's.
    private static bool Hides(MethodInfo derived, MethodInfo hidden)
        => derived.Name == hidden.Name
           && derived.DeclaringType is { } derivedType && hidden.DeclaringType is { } hiddenType && derivedType != hiddenType
           && derivedType.IsSubclassOf(hiddenType)
           && derived.GetParameters().Select(p => p.ParameterType).SequenceEqual(hidden.GetParameters().Select(p => p.ParameterType));

    private static CommandReturn ReturnKindOf(Type type)
    {
        if (type == typeof(void)) return CommandReturn.Void;
        if (typeof(Task).IsAssignableFrom(type)) return CommandReturn.Task;
        if (type == typeof(ValueTask)) return CommandReturn.ValueTask;
        if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(ValueTask<>)) return CommandReturn.ValueTaskOf;
        return CommandReturn.Value;   // the value is not sent
    }

    // A property that can be read and whose type can be sent.
    private static bool IsSendableProperty(PropertyInfo property)
        => property.GetMethod is { IsPublic: true, IsStatic: false }
           && property.GetIndexParameters().Length == 0
           && property.GetCustomAttribute<JsonIgnoreAttribute>() is null
           && IsSendable(property.PropertyType);

    // What a client can hold. A type is refused when it is a behaviour (delegate, task) or a service the view model uses.
    internal static bool IsSendable(Type type)
    {
        type = Nullable.GetUnderlyingType(type) ?? type;
        if (typeof(Delegate).IsAssignableFrom(type) || typeof(Task).IsAssignableFrom(type)
            || typeof(Type).IsAssignableFrom(type) || typeof(Stream).IsAssignableFrom(type)
            || typeof(IServiceProvider).IsAssignableFrom(type) || typeof(IRouter).IsAssignableFrom(type)
            || typeof(MemberInfo).IsAssignableFrom(type) || typeof(System.Security.Claims.ClaimsPrincipal).IsAssignableFrom(type))
            return false;
        if (type.IsGenericType && (type.GetGenericTypeDefinition() == typeof(ValueTask<>)) || type == typeof(ValueTask))
            return false;

        return !type.IsPointer && !type.IsByRef && !type.IsGenericTypeDefinition;
    }

    internal static bool IsScalar(Type type)
    {
        type = Nullable.GetUnderlyingType(type) ?? type;
        return type.IsPrimitive || type.IsEnum
            || FromString(type) is not null
            || type == typeof(string) || type == typeof(decimal) || type == typeof(Guid)
            || type == typeof(DateTime) || type == typeof(DateTimeOffset) || type == typeof(TimeSpan)
            || type == typeof(DateOnly) || type == typeof(TimeOnly) || type == typeof(Uri);
    }

    /// <summary>
    /// The conversion from a string of a type that is a name (<c>RouteTransition</c>: <c>RouteTransition t = "Fade"</c>), or
    /// <see langword="null"/>. Such a value is sent as its text, and a client sets it with a text.
    /// </summary>
    public static MethodInfo? FromString(Type type)
    {
        ArgumentNullException.ThrowIfNull(type);
        type = Nullable.GetUnderlyingType(type) ?? type;
        if (type == typeof(string) || type.IsPrimitive || type.IsEnum)
            return null;

        return type.GetMethod("op_Implicit", BindingFlags.Public | BindingFlags.Static, [typeof(string)]) is { } method && method.ReturnType == type
            ? method
            : null;
    }
}

internal enum CommandReturn { Void, Task, ValueTask, ValueTaskOf, Value }

internal sealed record StateMember(PropertyInfo Property, string Name, bool Settable, IAuthorizeData[] Authorize, bool AllowAnonymous);

internal sealed record CommandMember(MethodInfo Method, ParameterInfo[] Parameters, CommandReturn Returns, IAuthorizeData[] Authorize, bool AllowAnonymous);

internal sealed record TypeShape(
    Type Type,
    StateMember[] State,
    IReadOnlyDictionary<string, StateMember> ByName,
    IReadOnlyDictionary<string, CommandMember> Commands,
    IAuthorizeData[] Authorize,
    bool AllowAnonymous);
