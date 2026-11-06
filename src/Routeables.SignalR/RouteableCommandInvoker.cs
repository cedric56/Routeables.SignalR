using System.Collections;
using System.Reflection;
using System.Text.Json;
using System.Windows.Input;

namespace Routeables.SignalR;

/// <summary>
/// Calls a command of a view model with the arguments a client sent as JSON. A command is a method marked <see cref="RouteableCommandAttribute"/>,
/// or an <see cref="ICommand"/> reached by its path in the state (<c>adopt</c>, <c>choices.2.adoptCommand</c>).
/// </summary>
public static class RouteableCommandInvoker
{
    /// <summary>Calls the command named <paramref name="command"/> on <paramref name="viewModel"/>.</summary>
    /// <param name="viewModel">The view model that is current.</param>
    /// <param name="command">The client name of the command.</param>
    /// <param name="arguments">The arguments, in the order of the parameters.</param>
    /// <returns>Whether it was called, and why not when it was not.</returns>
    public static async Task<RouteableAck> InvokeAsync(object viewModel, string command, IReadOnlyList<JsonElement>? arguments)
    {
        ArgumentNullException.ThrowIfNull(viewModel);

        if (!RouteableStateProjector.Commands(viewModel.GetType()).TryGetValue(command, out var method))
            return await InvokeCommandAsync(viewModel, command, arguments).ConfigureAwait(false);

        if (!TryReadArguments(method, arguments ?? [], out var values, out var error))
            return RouteableAck.Fail(error!);

        try
        {
            var result = method.Invoke(viewModel, values);
            if (result is Task task)
                await task.ConfigureAwait(false);
            return RouteableAck.Ok;
        }
        catch (TargetInvocationException exception) when (exception.InnerException is not null)
        {
            return RouteableAck.Fail(exception.InnerException.Message);
        }
    }

    /// <summary>Finds the <see cref="ICommand"/> at <paramref name="path"/> in the state of <paramref name="viewModel"/>.</summary>
    public static ICommand? Resolve(object viewModel, string path)
    {
        object? current = viewModel;
        foreach (var segment in path.Split('.'))
        {
            current = current switch
            {
                null => null,
                IDictionary dictionary => dictionary.Contains(segment) ? dictionary[segment] : null,
                IList list => int.TryParse(segment, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var index) && index < list.Count ? list[index] : null,
                _ => RouteableStateProjector.StateProperties(current.GetType())
                        .FirstOrDefault(p => string.Equals(RouteableStateProjector.ClientName(p.Name), segment, StringComparison.Ordinal))?.GetValue(current)
            };
            if (current is null)
                return null;
        }
        return current as ICommand;
    }

    private static async Task<RouteableAck> InvokeCommandAsync(object viewModel, string path, IReadOnlyList<JsonElement>? arguments)
    {
        ICommand? command;
        try { command = Resolve(viewModel, path); }
        catch (TargetInvocationException exception) { return RouteableAck.Fail(exception.InnerException?.Message ?? exception.Message); }

        if (command is null)
            return RouteableAck.Fail($"'{viewModel.GetType().Name}' has no command '{path}'.");

        var parameter = arguments is { Count: > 0 } ? ToParameter(arguments[0]) : null;
        if (!command.CanExecute(parameter))
            return RouteableAck.Fail($"The command '{path}' cannot run now.");

        try
        {
            // A command that offers ExecuteAsync() (AsyncCommand) is awaited, so that its failure reaches the client: Execute is async void.
            if (command.GetType().GetMethod("ExecuteAsync", BindingFlags.Public | BindingFlags.Instance, Type.EmptyTypes) is { } execute
                && typeof(Task).IsAssignableFrom(execute.ReturnType))
                await ((Task)execute.Invoke(command, null)!).ConfigureAwait(false);
            else
                command.Execute(parameter);
            return RouteableAck.Ok;
        }
        catch (TargetInvocationException exception) when (exception.InnerException is not null)
        {
            return RouteableAck.Fail(exception.InnerException.Message);
        }
    }

    private static object? ToParameter(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => value.GetString(),
        JsonValueKind.Number => value.TryGetInt32(out var number) ? number : value.GetDouble(),
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        _ => null
    };

    private static bool TryReadArguments(MethodInfo method, IReadOnlyList<JsonElement> arguments, out object?[] values, out string? error)
    {
        var parameters = method.GetParameters();
        values = new object?[parameters.Length];
        error = null;

        if (arguments.Count > parameters.Length)
        {
            error = $"'{method.Name}' takes {parameters.Length} argument(s), {arguments.Count} given.";
            return false;
        }

        for (var i = 0; i < parameters.Length; i++)
        {
            if (i >= arguments.Count)
            {
                if (!parameters[i].HasDefaultValue)
                {
                    error = $"'{method.Name}' needs its '{parameters[i].Name}' argument.";
                    return false;
                }
                values[i] = parameters[i].DefaultValue;
                continue;
            }

            try
            {
                values[i] = arguments[i].Deserialize(parameters[i].ParameterType, JsonSerializerOptions.Web);
            }
            catch (JsonException)
            {
                error = $"The '{parameters[i].Name}' argument of '{method.Name}' is not a {parameters[i].ParameterType.Name}.";
                return false;
            }
        }
        return true;
    }
}
