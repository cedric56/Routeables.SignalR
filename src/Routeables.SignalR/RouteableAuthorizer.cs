using System.Reflection;
using System.Runtime.CompilerServices;
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

    /// <summary>Whether reading <paramref name="member"/> of an object of the state asks for an authorization.</summary>
    public static bool Guards(TypeShape shape, StateMember member)
        => !member.AllowAnonymous && (shape.Authorize.Length > 0 || member.Authorize.Length > 0);

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

/// <summary>
/// What the user may read of the objects inside the state of a target: the properties of a nested type that ask for an authorization
/// (<c>[Authorize]</c> or <c>[RouteableAuthorize]</c> on the type or on the property), as for a command at the same path.
/// </summary>
/// <remarks>
/// The projection does not wait, the authorization of ASP.NET Core does: a property is decided once for each object, and until it is decided it is
/// not sent. <see cref="DecideAsync"/> decides what the projection met and tells which properties of the view model have something more to show.
/// The decisions belong to a page: <see cref="Clear"/> forgets them when another page (or the same one sent again) is shown.
/// </remarks>
internal sealed class NestedReadAccess(RouteableAuthorizer authorizer)
{
    // By object for a reference type (a policy may look at the object), by type for a value type (each reading boxes a new one).
    private ConditionalWeakTable<object, Dictionary<StateMember, bool>> _decisions = new();
    private readonly List<(string Property, object Owner, TypeShape Shape, StateMember Member)> _unknown = [];

    public bool HasUnknown => _unknown.Count > 0;

    /// <summary>The guard given to the projection.</summary>
    public bool Allows(string property, object owner, TypeShape shape, StateMember member)
    {
        if (!RouteableAuthorizer.Guards(shape, member))
            return true;
        if (_decisions.TryGetValue(KeyOf(owner, shape), out var decided) && decided.TryGetValue(member, out var allowed))
            return allowed;
        _unknown.Add((property, owner, shape, member));
        return false;   // not decided yet: not sent
    }

    /// <summary>Decides what the projections met since the last call.</summary>
    /// <returns>The client names of the properties of the view model in which something the user may read was hidden.</returns>
    public async ValueTask<HashSet<string>> DecideAsync(Action<Exception> unhandled)
    {
        var decisions = _decisions;
        var reveal = new HashSet<string>(StringComparer.Ordinal);
        while (_unknown.Count > 0)
        {
            var unknown = _unknown.ToArray();
            _unknown.Clear();
            foreach (var (property, owner, shape, member) in unknown)
            {
                var decided = decisions.GetValue(KeyOf(owner, shape), static _ => new Dictionary<StateMember, bool>());
                if (!decided.TryGetValue(member, out var allowed))
                {
                    try { allowed = await authorizer.CanUseAsync(owner, shape, member).ConfigureAwait(true); }
                    catch (Exception exception) when (exception is not OutOfMemoryException)
                    {
                        unhandled(exception);
                        allowed = false;   // a policy that fails never opens a door
                    }
                    decided[member] = allowed;
                }
                if (allowed)
                    reveal.Add(property);
            }
            if (!ReferenceEquals(decisions, _decisions))
                return [];   // forgotten meanwhile: another page
        }
        return reveal;
    }

    /// <summary>Forgets the decisions (another page, or the same one sent again: the user is asked again).</summary>
    public void Clear()
    {
        _decisions = new();
        _unknown.Clear();
    }

    private static object KeyOf(object owner, TypeShape shape) => owner.GetType().IsValueType ? shape : owner;
}
