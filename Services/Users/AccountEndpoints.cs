using System.Security.Claims;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace fuseraft.Web.Services.Users;

/// <summary>The form posts behind the sign-in, setup and sign-out pages, which Blazor's interactive pages can't set cookies from.</summary>
public static class AccountEndpoints
{
    public static void MapAccountEndpoints(this WebApplication app)
    {
        var account = app.MapGroup("/account");

        account.MapPost("/login", async (HttpContext http, UserStore users, IOptions<AuthOptions> auth,
            [FromForm] string? userName, [FromForm] string? password, [FromForm] string? returnUrl) =>
        {
            if (!auth.Value.LocalAccounts)
                return Results.Redirect(LoginUrl("Password sign-in is turned off.", returnUrl));
            if (users.CheckPassword(userName, password) is not { } user)
                return Results.Redirect(LoginUrl("Wrong username or password.", returnUrl));
            await SignInAsync(http, user, "password");
            return Results.LocalRedirect(SafeReturn(returnUrl));
        }).AllowAnonymous().RequireRateLimiting("sign-in");

        account.MapPost("/setup", async (HttpContext http, UserStore users, [FromForm] string? code,
            [FromForm] string? userName, [FromForm] string? password, [FromForm] string? confirm) =>
        {
            string Back(string error) => $"/setup?code={Uri.EscapeDataString(code ?? "")}&error={Uri.EscapeDataString(error)}";

            if (!users.IsEmpty || !users.CheckSetupCode(code))
                return Results.Redirect("/login?error=" + Uri.EscapeDataString("Setup is finished or the code is wrong."));
            if (password != confirm) return Results.Redirect(Back("The passwords don't match."));
            WebUser user;
            try { user = users.Create(userName ?? "", password ?? "", isAdmin: true); }
            catch (ArgumentException ex) { return Results.Redirect(Back(ex.Message)); }
            await SignInAsync(http, user, "password");
            return Results.LocalRedirect("/");
        }).AllowAnonymous().RequireRateLimiting("sign-in");

        account.MapGet("/oidc", (HttpContext http, IOptions<AuthOptions> auth, string? returnUrl, bool? link) =>
        {
            if (!auth.Value.Oidc.Enabled) return Results.Redirect(LoginUrl("Single sign-on isn't configured.", returnUrl));
            var properties = new AuthenticationProperties { RedirectUri = SafeReturn(returnUrl) };
            if (link == true && http.User.FindFirstValue(ClaimTypes.NameIdentifier) is { } id)
                properties.Items[OidcSignIn.LinkItem] = id;
            return Results.Challenge(properties, [OpenIdConnectDefaults.AuthenticationScheme]);
        }).AllowAnonymous();

        account.MapPost("/logout", async (HttpContext http) =>
        {
            await http.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            return Results.LocalRedirect("/login");
        }).WithMetadata(new RequireAntiforgeryTokenAttribute());
    }

    private static Task SignInAsync(HttpContext http, WebUser user, string method) =>
        http.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, UserClaims.Principal(user, method));

    private static string LoginUrl(string error, string? returnUrl) =>
        $"/login?error={Uri.EscapeDataString(error)}&returnUrl={Uri.EscapeDataString(SafeReturn(returnUrl))}";

    /// <summary>Only same-site paths, so a crafted link can't bounce a fresh sign-in to another site.</summary>
    internal static string SafeReturn(string? returnUrl) =>
        returnUrl is { Length: > 0 } && returnUrl[0] == '/' && !returnUrl.StartsWith("//") && !returnUrl.StartsWith("/\\")
            ? returnUrl
            : "/";
}
