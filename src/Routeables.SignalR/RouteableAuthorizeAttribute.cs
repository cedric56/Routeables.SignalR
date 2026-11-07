using Microsoft.AspNetCore.Authorization;

namespace Routeables.SignalR;

/// <summary>
/// The <c>[Authorize]</c> of a property: the property is neither sent nor set, and the command it holds not called, for a user who does not meet
/// it. ASP.NET Core's <see cref="AuthorizeAttribute"/> cannot be put on a property; on a class or a method, either attribute works.
/// </summary>
/// <example>
/// <code>
/// [RouteableState]
/// public sealed class OrderViewModel
/// {
///     [RouteableAuthorize(Roles = "manager")] public decimal Margin { get; }
/// }
/// </code>
/// </example>
[AttributeUsage(AttributeTargets.Property | AttributeTargets.Method | AttributeTargets.Class, AllowMultiple = true, Inherited = true)]
public sealed class RouteableAuthorizeAttribute : Attribute, IAuthorizeData
{
    /// <summary>Requires the user to meet the default policy (an authenticated user, unless the application changed it).</summary>
    public RouteableAuthorizeAttribute()
    {
    }

    /// <summary>Requires the user to meet the policy named <paramref name="policy"/>.</summary>
    public RouteableAuthorizeAttribute(string policy) => Policy = policy;

    /// <inheritdoc />
    public string? Policy { get; set; }

    /// <inheritdoc />
    public string? Roles { get; set; }

    /// <inheritdoc />
    public string? AuthenticationSchemes { get; set; }
}
