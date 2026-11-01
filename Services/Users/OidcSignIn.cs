using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;

namespace fuseraft.Web.Services.Users;

/// <summary>
/// Signs people in through an OIDC provider, matching its <c>iss</c> and <c>sub</c> to a linked account. An unknown
/// identity gets an account only when its verified email is allowed (<see cref="OidcOptions.AutoProvision"/>).
/// </summary>
public static class OidcSignIn
{
    /// <summary>Set on a challenge started by a signed-in user, to link the identity to their account instead.</summary>
    public const string LinkItem = "fuseraft:link";

    public static void Configure(OpenIdConnectOptions o, OidcOptions config)
    {
        o.SignInScheme     = CookieAuthenticationDefaults.AuthenticationScheme;
        o.Authority        = config.Authority;
        o.ClientId         = config.ClientId;
        o.ClientSecret     = config.ClientSecret;
        o.ResponseType     = "code";
        o.UsePkce          = true;
        o.MapInboundClaims = false;
        o.SaveTokens       = false;
        o.CallbackPath     = "/signin-oidc";
        o.SignedOutCallbackPath = "/signout-callback-oidc";
        o.GetClaimsFromUserInfoEndpoint = true;
        o.Scope.Clear();
        foreach (var scope in config.Scopes) o.Scope.Add(scope);

        o.Events.OnTokenValidated = ctx =>
        {
            var users = ctx.HttpContext.RequestServices.GetRequiredService<UserStore>();
            var log   = ctx.HttpContext.RequestServices.GetRequiredService<ILogger<UserStore>>();
            var (user, problem) = Resolve(users, config, ctx.Principal!, ctx.Properties?.Items.TryGetValue(LinkItem, out var link) == true ? link : null);
            if (user is null)
            {
                log.LogWarning("OIDC sign-in refused: {Problem}", problem);
                ctx.Fail(problem);
                return Task.CompletedTask;
            }
            ctx.Principal = UserClaims.Principal(user, "oidc");
            return Task.CompletedTask;
        };
        o.Events.OnRemoteFailure = ctx =>
        {
            ctx.Response.Redirect("/login?error=" + Uri.EscapeDataString(ctx.Failure?.Message ?? "Single sign-on failed."));
            ctx.HandleResponse();
            return Task.CompletedTask;
        };
    }

    /// <summary>The account an OIDC identity signs in as, or why it may not.</summary>
    internal static (WebUser? User, string Problem) Resolve(UserStore users, OidcOptions config, ClaimsPrincipal identity, string? linkUserId)
    {
        var subClaim = identity.FindFirst("sub");
        if (subClaim is null) return (null, "The provider didn't send a subject.");
        var issuer  = subClaim.Issuer;
        var subject = subClaim.Value;
        // An email the provider says is unverified could be anyone's, so it never grants anything.
        var email = identity.FindFirstValue("email_verified") is "false" or "False"
            ? null
            : identity.FindFirstValue("email")?.Trim().ToLowerInvariant();

        if (linkUserId is not null)
        {
            if (users.FindByLogin(issuer, subject) is { } taken && taken.Id != linkUserId)
                return (null, "That sign-on identity is already linked to another account.");
            var target = users.Find(linkUserId);
            if (target is null) return (null, "Your account no longer exists.");
            if (!target.Logins.Any(l => l.Issuer == issuer && l.Subject == subject))
                users.Update(target.Id, u => u.Logins.Add(new ExternalLogin(issuer, subject, email)));
            return Usable(target);
        }

        if (users.FindByLogin(issuer, subject) is { } linked)
            return Usable(linked);

        var isAdminEmail = email is not null && config.AdminEmails.Any(a => a.Trim().Equals(email, StringComparison.OrdinalIgnoreCase));
        var allowed      = email is not null && (isAdminEmail || (config.AutoProvision && DomainAllowed(config, email)));
        if (!allowed && !(users.IsEmpty && isAdminEmail))
            return (null, email is null
                ? "No account is linked to this sign-on identity, and the provider sent no verified email."
                : $"No account is linked to {email}. Ask an admin to create one, then link it from your Account page.");

        var name = UniqueName(users, email!.Split('@')[0]);
        var user = users.Create(name, password: null, isAdmin: isAdminEmail,
            displayName: identity.FindFirstValue("name"), login: new ExternalLogin(issuer, subject, email));
        return (user, "");
    }

    private static (WebUser?, string) Usable(WebUser user) =>
        user.Disabled ? (null, $"The account '{user.UserName}' is disabled.") : (user, "");

    private static bool DomainAllowed(OidcOptions config, string email)
    {
        var domain = email[(email.LastIndexOf('@') + 1)..];
        return config.AllowedEmailDomains.Any(d => d.Trim().TrimStart('@').Equals(domain, StringComparison.OrdinalIgnoreCase));
    }

    private static string UniqueName(UserStore users, string localPart)
    {
        var baseName = new string(localPart.ToLowerInvariant().Where(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-').ToArray())
            .TrimStart('.', '_', '-');
        if (baseName.Length == 0) baseName = "user";
        if (baseName.Length > 56) baseName = baseName[..56];
        var name = baseName;
        for (var i = 2; users.ValidateNewName(name) is not null; i++) name = $"{baseName}{i}";
        return name;
    }
}
