using System.Reflection;
using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.DependencyInjection;

namespace Routeables.SignalR;

/// <summary>
/// The connection a session serves: who uses it. One per connection (scoped): a view model or a service of the connection asks for it to know
/// the user, and the authorization of the view models is evaluated against <see cref="User"/> at every call.
/// </summary>
public sealed class RouteableConnection
{
    private static readonly ClaimsPrincipal Anonymous = new(new ClaimsIdentity());

    /// <summary>The user of the connection; an anonymous principal when the connection is not authenticated.</summary>
    public ClaimsPrincipal User { get; internal set; } = Anonymous;

    /// <summary>The SignalR connection the session is attached to now (it changes when the session is resumed).</summary>
    public string? ConnectionId { get; internal set; }

    internal void Attach(string? connectionId, ClaimsPrincipal? user)
    {
        ConnectionId = connectionId;
        User = user ?? Anonymous;
    }
}

/// <summary>
/// Evaluates the <c>[Authorize]</c> and <c>[AllowAnonymous]</c> of view models (the class), of their commands (the method) and of their properties,
/// with the authorization of ASP.NET Core (<see cref="IAuthorizationService"/>, the policies of the application).
/// </summary>
/// <remarks>
/// An <c>[AllowAnonymous]</c> on a command or a property lets anybody through, as in ASP.NET Core. When a member asks for authorization and no
/// authorization service is registered, it is refused: the absence of a service never opens a door.
/// </remarks>
internal sealed class RouteableAuthorizer(IServiceProvider services, RouteableConnection connection)
{
    private readonly IAuthorizationService? _service = services.GetService<IAuthorizationService>();
    private readonly IAuthorizationPolicyProvider? _policies = services.GetService<IAuthorizationPolicyProvider>();

    public ClaimsPrincipal User => connection.User;

    /// <summary>Whether the user may see the view model (its class).</summary>
    public ValueTask<bool> CanSeeAsync(object viewModel, TypeShape shape)
        => shape.AllowAnonymous ? ValueTask.FromResult(true) : AuthorizeAsync(viewModel, shape.Authorize);

    /// <summary>Whether the user may call the command.</summary>
    public ValueTask<bool> CanInvokeAsync(object viewModel, TypeShape shape, CommandMember command)
        => command.AllowAnonymous ? ValueTask.FromResult(true) : AuthorizeAsync(viewModel, [.. shape.Authorize, .. command.Authorize]);

    /// <summary>Whether the user may read or set the property (the class and the property).</summary>
    public ValueTask<bool> CanUseAsync(object viewModel, TypeShape shape, StateMember property)
        => property.AllowAnonymous ? ValueTask.FromResult(true) : AuthorizeAsync(viewModel, [.. shape.Authorize, .. property.Authorize]);

    /// <summary>The properties of the view model the user may not read: those that ask more than the class.</summary>
    public async ValueTask<HashSet<PropertyInfo>> HiddenAsync(object viewModel, TypeShape shape)
    {
        var hidden = new HashSet<PropertyInfo>();
        foreach (var member in shape.State)
        {
            if (member.Authorize.Length == 0 || member.AllowAnonymous)
                continue;
            if (!await AuthorizeAsync(viewModel, member.Authorize).ConfigureAwait(true))
                hidden.Add(member.Property);
        }
        return hidden;
    }

    public async ValueTask<bool> AuthorizeAsync(object resource, IReadOnlyCollection<IAuthorizeData> data)
    {
        if (data.Count == 0)
            return true;
        if (_service is null || _policies is null)
            return false;

        var policy = await AuthorizationPolicy.CombineAsync(_policies, data).ConfigureAwait(true);
        if (policy is null)
            return true;

        var result = await _service.AuthorizeAsync(connection.User, resource, policy).ConfigureAwait(true);
        return result.Succeeded;
    }
}
