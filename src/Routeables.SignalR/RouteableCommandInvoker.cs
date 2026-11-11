using System.Collections;
using System.Reflection;
using System.Text.Json;
using System.Windows.Input;

namespace Routeables.SignalR;

/// <summary>
/// Calls a command of a view model, or sets one of its properties, with what a client sent as JSON. A command is a method marked
/// <see cref="RouteableCommandAttribute"/>, or an <see cref="ICommand"/> of the exposed state reached by its path (<c>adopt</c>,
/// <c>choices.2.adoptCommand</c>). The arguments are checked against the limits of <see cref="RouteablesOptions"/>, the authorization
/// of the member is evaluated, and every failure becomes a failed <see cref="RouteableAck"/>: nothing a client sends makes the hub throw.
/// </summary>
public sealed class RouteableCommandInvoker
{
    private const int MaxNameLength = 512;
    private static readonly JsonSerializerOptions Web = new(JsonSerializerOptions.Web);

    private readonly RouteableSchema _schema;
    private readonly JsonSerializerOptions _json;

    /// <summary>Creates the invoker of a schema.</summary>
    /// <param name="schema">What the client may call and set.</param>
    /// <param name="detailedErrors">Whether the message of any exception is sent; otherwise only that of a <see cref="RouteableException"/>.</param>
    public RouteableCommandInvoker(RouteableSchema schema, bool detailedErrors = false)
    {
        _schema = schema ?? throw new ArgumentNullException(nameof(schema));
        DetailedErrors = detailedErrors;
        _json = new JsonSerializerOptions(Web) { MaxDepth = Math.Max(1, schema.Options.MaxArgumentDepth) };
    }

    /// <summary>Whether the message of any exception is sent to the client.</summary>
    public bool DetailedErrors { get; }

    private RouteablesOptions Options => _schema.Options;

    /// <summary>Calls the command named <paramref name="command"/> on <paramref name="viewModel"/>, without authorization.</summary>
    public async Task<RouteableAck> InvokeAsync(object viewModel, string command, IReadOnlyList<JsonElement>? arguments)
        => (await InvokeAsync(viewModel, command, arguments, null, default).ConfigureAwait(true)).Ack;

    /// <summary>Sets the property named <paramref name="property"/> (its client name) of <paramref name="viewModel"/>, without authorization.</summary>
    public async Task<RouteableAck> SetAsync(object viewModel, string property, JsonElement value)
        => (await SetAsync(viewModel, property, value, null).ConfigureAwait(true)).Ack;

    internal async Task<Outcome> InvokeAsync(object viewModel, string command, IReadOnlyList<JsonElement>? arguments, RouteableAuthorizer? authorizer, CancellationToken lifetime = default)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        if (string.IsNullOrEmpty(command) || command.Length > MaxNameLength)
            return Outcome.Fail("The name of the command is empty or too long.", RouteableErrorCodes.InvalidArgument);

        arguments ??= [];
        if (CheckArguments(arguments) is { } invalid)
            return Outcome.Fail(invalid, RouteableErrorCodes.InvalidArgument);

        var shape = _schema.Shape(viewModel.GetType());
        if (!shape.Commands.TryGetValue(command, out var method))
            return await InvokeCommandAsync(viewModel, shape, command, arguments, authorizer).ConfigureAwait(true);

        if (authorizer is not null && !await authorizer.CanInvokeAsync(viewModel, shape, method).ConfigureAwait(true))
            return Outcome.Fail($"You may not call '{command}'.", RouteableErrorCodes.Unauthorized);

        if (!TryReadArguments(method, arguments, lifetime, out var values, out var error))
            return Outcome.Fail(error!, RouteableErrorCodes.InvalidArgument);

        try
        {
            var result = method.Method.Invoke(viewModel, BindingFlags.DoNotWrapExceptions, null, values, null);
            switch (method.Returns)
            {
                case CommandReturn.Task:
                    await ((Task)result!).ConfigureAwait(true);
                    break;
                case CommandReturn.ValueTask:
                    await ((ValueTask)result!).ConfigureAwait(true);
                    break;
                case CommandReturn.ValueTaskOf:
                    await (Task)AsTaskOf(result!.GetType()).Invoke(result, null)!;
                    break;
            }
            return Outcome.Ok;
        }
        catch (Exception exception)
        {
            return Failure(exception);
        }
    }

    internal async Task<Outcome> SetAsync(object viewModel, string property, JsonElement value, RouteableAuthorizer? authorizer)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        if (string.IsNullOrEmpty(property) || property.Length > MaxNameLength)
            return Outcome.Fail("The name of the property is empty or too long.", RouteableErrorCodes.InvalidArgument);
        if (CheckArgument(value) is { } invalid)
            return Outcome.Fail(invalid, RouteableErrorCodes.InvalidArgument);

        var shape = _schema.Shape(viewModel.GetType());
        if (!shape.ByName.TryGetValue(property, out var member) || !member.Settable)
            return Outcome.Fail($"'{viewModel.GetType().Name}' has no property '{property}' that can be set.", RouteableErrorCodes.NotFound);

        if (authorizer is not null && !await authorizer.CanUseAsync(viewModel, shape, member).ConfigureAwait(true))
            return Outcome.Fail($"You may not set '{property}'.", RouteableErrorCodes.Unauthorized);

        object? converted;
        try
        {
            converted = RouteableSchema.FromString(member.Property.PropertyType) is { } fromString && value.ValueKind == JsonValueKind.String
                ? fromString.Invoke(null, BindingFlags.DoNotWrapExceptions, null, [value.GetString()], null)
                : value.Deserialize(member.Property.PropertyType, _json);
        }
        catch (Exception exception) when (exception is JsonException or NotSupportedException or ArgumentException or InvalidOperationException or FormatException or OverflowException)
        {
            return Outcome.Fail($"The value of '{property}' is not a {member.Property.PropertyType.Name}.", RouteableErrorCodes.InvalidArgument);
        }

        if (converted is null && member.Property.PropertyType.IsValueType && Nullable.GetUnderlyingType(member.Property.PropertyType) is null)
            return Outcome.Fail($"The value of '{property}' is not a {member.Property.PropertyType.Name}.", RouteableErrorCodes.InvalidArgument);

        try
        {
            member.Property.SetMethod!.Invoke(viewModel, BindingFlags.DoNotWrapExceptions, null, [converted], null);
            return Outcome.Ok;
        }
        catch (Exception exception)
        {
            return Failure(exception);
        }
    }

    /// <summary>Finds the <see cref="ICommand"/> at <paramref name="path"/> in the exposed state of <paramref name="viewModel"/>.</summary>
    public ICommand? Resolve(object viewModel, string path) => Resolve(viewModel, path, null);

    private ICommand? Resolve(object viewModel, string path, List<(object Owner, TypeShape Shape, StateMember Member)>? traversed)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        var segments = path.Split('.');
        if (segments.Length > Options.MaxDepth * 2 + 1)
            return null;

        object? current = viewModel;
        for (var at = 0; at < segments.Length; at++)
        {
            var segment = segments[at];
            switch (current)
            {
                case null:
                    return null;
                case ICommand:
                    return null;   // a path goes on past a command
                case IDictionary dictionary:
                    current = Entry(dictionary, segments, ref at);
                    break;
                case IList list:
                    current = int.TryParse(segment, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var index) && index < list.Count ? list[index] : null;
                    break;
                default:
                    var shape = _schema.Shape(current.GetType());
                    if (!shape.ByName.TryGetValue(segment, out var member))
                        return null;
                    traversed?.Add((current, shape, member));
                    current = RouteableStateProjector.TryRead(current, member.Property, out var value) ? value : null;
                    break;
            }
        }
        return current as ICommand;
    }

    /// <summary>
    /// The value of the entry the path names, as the projector named it (the key as invariant text): an <see cref="int"/> key is found by
    /// its digits, and a key with dots takes as many segments as it has parts (the longest key that matches wins).
    /// </summary>
    private static object? Entry(IDictionary dictionary, string[] segments, ref int at)
    {
        var names = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (DictionaryEntry entry in dictionary)
            names.TryAdd(RouteableStateProjector.KeyText(entry.Key), entry.Value);   // the first of two keys with one text, as the projector
        for (var last = segments.Length - 1; last >= at; last--)
        {
            var name = last == at ? segments[at] : string.Join('.', segments, at, last - at + 1);
            if (names.TryGetValue(name, out var value))
            {
                at = last;
                return value;
            }
        }
        return null;
    }

    private async Task<Outcome> InvokeCommandAsync(object viewModel, TypeShape shape, string path, IReadOnlyList<JsonElement> arguments, RouteableAuthorizer? authorizer)
    {
        if (arguments.Count > 1)
            return Outcome.Fail($"The command '{path}' takes at most one argument, {arguments.Count} given.", RouteableErrorCodes.InvalidArgument);

        var traversed = new List<(object Owner, TypeShape Shape, StateMember Member)>();
        ICommand? command;
        try { command = Resolve(viewModel, path, traversed); }
        catch (Exception exception) { return Failure(exception); }

        if (command is null)
            return Outcome.Fail($"'{viewModel.GetType().Name}' has no command '{path}'.", RouteableErrorCodes.NotFound);

        if (authorizer is not null)
        {
            if (!await authorizer.CanSeeAsync(viewModel, shape).ConfigureAwait(true))
                return Outcome.Fail($"You may not call '{path}'.", RouteableErrorCodes.Unauthorized);
            foreach (var (owner, ownerShape, member) in traversed)
                if (!await authorizer.CanUseAsync(owner, ownerShape, member).ConfigureAwait(true))
                    return Outcome.Fail($"You may not call '{path}'.", RouteableErrorCodes.Unauthorized);
        }

        var parameter = arguments.Count > 0 ? ToParameter(arguments[0]) : null;
        try
        {
            if (!command.CanExecute(parameter))
                return Outcome.Fail($"The command '{path}' cannot run now.", RouteableErrorCodes.Disabled);

            // A command that offers ExecuteAsync (AsyncCommand, AsyncRelayCommand) is awaited, so that its failure reaches the client: Execute is async void.
            if (FindExecuteAsync(command.GetType(), parameter) is { } execute)
            {
                object?[] values = execute.GetParameters().Length == 0 ? [] : [parameter];
                if (execute.Invoke(command, BindingFlags.DoNotWrapExceptions, null, values, null) is Task task)
                    await task.ConfigureAwait(true);
            }
            else
                command.Execute(parameter);
            return Outcome.Ok;
        }
        catch (Exception exception)
        {
            return Failure(exception);
        }
    }

    // The reflection a call needs, found once per type.
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<Type, MethodInfo> AsTasks = new();
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<Type, (MethodInfo Method, ParameterInfo[] Parameters)[]> ExecuteAsyncs = new();

    private static MethodInfo AsTaskOf(Type valueTaskType)
        => AsTasks.GetOrAdd(valueTaskType, static t => t.GetMethod(nameof(ValueTask<int>.AsTask))!);

    private static MethodInfo? FindExecuteAsync(Type type, object? parameter)
    {
        var candidates = ExecuteAsyncs.GetOrAdd(type, static t => [.. t.GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .Where(m => m.Name == "ExecuteAsync" && typeof(Task).IsAssignableFrom(m.ReturnType) && !m.IsGenericMethodDefinition)
            .Select(m => (m, m.GetParameters()))]);
        foreach (var (method, parameters) in candidates)
        {
            if (parameters.Length == 0 && parameter is null)
                return method;
            if (parameters.Length == 1 && (parameter is null ? !parameters[0].ParameterType.IsValueType || Nullable.GetUnderlyingType(parameters[0].ParameterType) is not null : parameters[0].ParameterType.IsInstanceOfType(parameter)))
                return method;
        }
        return null;
    }

    private Outcome Failure(Exception exception)
    {
        if (exception is TargetInvocationException { InnerException: { } inner })
            exception = inner;

        return exception switch
        {
            OperationCanceledException => new Outcome(RouteableAck.Fail("The command was cancelled.", RouteableErrorCodes.Cancelled), null),
            RouteableException routeable => new Outcome(RouteableAck.Fail(routeable.Message), null),
            _ => new Outcome(RouteableAck.Fail(DetailedErrors ? exception.Message : "The command failed."), exception),
        };
    }

    private static object? ToParameter(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => value.GetString(),
        JsonValueKind.Number => value.TryGetInt32(out var number) ? number : value.GetDouble(),
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        _ => null
    };

    private string? CheckArguments(IReadOnlyList<JsonElement> arguments)
    {
        if (arguments.Count > 32)
            return $"At most 32 arguments can be given, {arguments.Count} given.";
        foreach (var argument in arguments)
            if (CheckArgument(argument) is { } error)
                return error;
        return null;
    }

    /// <summary>Checks the size, the number of values and the depth of an argument before anything reads it.</summary>
    private string? CheckArgument(JsonElement argument)
    {
        if (argument.ValueKind == JsonValueKind.Undefined)
            return "An argument is missing.";
        if (argument.GetRawText().Length > Options.MaxArgumentLength)
            return $"An argument is longer than {Options.MaxArgumentLength} characters.";

        var items = 0;
        var stack = new Stack<(JsonElement Element, int Depth)>();
        stack.Push((argument, 1));
        while (stack.Count > 0)
        {
            var (element, depth) = stack.Pop();
            if (depth > Options.MaxArgumentDepth)
                return $"An argument is nested more than {Options.MaxArgumentDepth} levels deep.";
            switch (element.ValueKind)
            {
                case JsonValueKind.Array:
                    foreach (var item in element.EnumerateArray())
                    {
                        if (++items > Options.MaxArgumentItems)
                            return $"An argument holds more than {Options.MaxArgumentItems} values.";
                        stack.Push((item, depth + 1));
                    }
                    break;
                case JsonValueKind.Object:
                    foreach (var property in element.EnumerateObject())
                    {
                        if (++items > Options.MaxArgumentItems)
                            return $"An argument holds more than {Options.MaxArgumentItems} values.";
                        stack.Push((property.Value, depth + 1));
                    }
                    break;
            }
        }
        return null;
    }

    private bool TryReadArguments(CommandMember method, IReadOnlyList<JsonElement> arguments, CancellationToken lifetime, out object?[] values, out string? error)
    {
        var parameters = method.Parameters;
        values = new object?[parameters.Length];
        error = null;

        // A CancellationToken parameter is not sent by the client: it is cancelled when the session ends.
        var sent = parameters.Count(p => p.ParameterType != typeof(CancellationToken));
        if (arguments.Count > sent)
        {
            error = $"'{method.Method.Name}' takes {sent} argument(s), {arguments.Count} given.";
            return false;
        }

        var next = 0;
        for (var i = 0; i < parameters.Length; i++)
        {
            var parameter = parameters[i];
            if (parameter.ParameterType == typeof(CancellationToken))
            {
                values[i] = lifetime;
                continue;
            }

            if (next >= arguments.Count)
            {
                if (!parameter.HasDefaultValue)
                {
                    error = $"'{method.Method.Name}' needs its '{parameter.Name}' argument.";
                    return false;
                }
                values[i] = parameter.DefaultValue;
                continue;
            }

            try
            {
                values[i] = arguments[next++].Deserialize(parameter.ParameterType, _json);
            }
            catch (Exception exception) when (exception is JsonException or NotSupportedException or ArgumentException or InvalidOperationException or FormatException or OverflowException)
            {
                error = $"The '{parameter.Name}' argument of '{method.Method.Name}' is not a {parameter.ParameterType.Name}.";
                return false;
            }

            if (values[i] is null && parameter.ParameterType.IsValueType && Nullable.GetUnderlyingType(parameter.ParameterType) is null)
            {
                error = $"The '{parameter.Name}' argument of '{method.Method.Name}' is not a {parameter.ParameterType.Name}.";
                return false;
            }
        }
        return true;
    }

    /// <summary>What a call gave: the answer, and the exception to log when it is not sent.</summary>
    internal readonly record struct Outcome(RouteableAck Ack, Exception? Unexpected)
    {
        public static Outcome Ok { get; } = new(RouteableAck.Ok, null);

        public static Outcome Fail(string error, string code) => new(RouteableAck.Fail(error, code), null);
    }
}
