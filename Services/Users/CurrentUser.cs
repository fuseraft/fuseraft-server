using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Components.Authorization;

namespace fuseraft.Web.Services.Users;

/// <summary>The signed-in account of this request or Blazor circuit.</summary>
public sealed class CurrentUser(AuthenticationStateProvider auth, IHttpContextAccessor http, UserStore users)
{
    private WebUser? _user;
    private string?  _stamp;

    /// <summary>
    /// The signed-in account. Every page requires one, so its absence is a bug, not a state to handle. A circuit
    /// outlives its cookie check, so an account disabled or signed out meanwhile stops working here too.
    /// </summary>
    public WebUser User
    {
        get
        {
            if (_user is null) (_user, _stamp) = Resolve();
            if (_user is not { Disabled: false } || _user.SecurityStamp != _stamp || users.Find(_user.Id) is null)
                throw new UnauthorizedAccessException("You're signed out. Reload the page to sign in again.");
            return _user;
        }
    }

    private (WebUser?, string?) Resolve()
    {
        // The circuit's state is set before any component runs, so this task is already complete there.
        var state     = auth.GetAuthenticationStateAsync();
        var principal = state.IsCompletedSuccessfully ? state.Result.User : http.HttpContext?.User;
        return (users.Find(principal?.FindFirstValue(ClaimTypes.NameIdentifier)), principal?.FindFirstValue(UserClaims.Stamp));
    }
}

public static class UserClaims
{
    public const string Stamp     = "fuseraft:stamp";
    public const string AdminRole = "admin";

    public static ClaimsPrincipal Principal(WebUser user, string method)
    {
        List<Claim> claims =
        [
            new(ClaimTypes.NameIdentifier, user.Id),
            new(ClaimTypes.Name, user.UserName),
            new(Stamp, user.SecurityStamp),
            new(ClaimTypes.AuthenticationMethod, method),
        ];
        if (user.IsAdmin) claims.Add(new Claim(ClaimTypes.Role, AdminRole));
        return new ClaimsPrincipal(new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme));
    }

    /// <summary>
    /// Ends a cookie whose account is gone, disabled or re-stamped, and refreshes its role when an admin
    /// changed it, so revoking access takes effect on the next request rather than when the cookie expires.
    /// </summary>
    public static async Task ValidateAsync(CookieValidatePrincipalContext context)
    {
        var users = context.HttpContext.RequestServices.GetRequiredService<UserStore>();
        var user  = users.Find(context.Principal?.FindFirstValue(ClaimTypes.NameIdentifier));
        if (user is not { Disabled: false } || user.SecurityStamp != context.Principal?.FindFirstValue(Stamp))
        {
            context.RejectPrincipal();
            await context.HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            return;
        }
        if (user.IsAdmin != context.Principal!.IsInRole(AdminRole))
        {
            context.ReplacePrincipal(Principal(user, context.Principal.FindFirstValue(ClaimTypes.AuthenticationMethod) ?? "password"));
            context.ShouldRenew = true;
        }
    }
}
