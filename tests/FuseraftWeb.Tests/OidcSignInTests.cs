using System.Security.Claims;
using fuseraft.Web.Services.Users;

namespace FuseraftWeb.Tests;

public sealed class OidcSignInTests : IDisposable
{
    private readonly Accounts _accounts = new();
    private const string Issuer = "https://idp.example";

    public void Dispose() => _accounts.Dispose();

    private static ClaimsPrincipal Identity(string sub, string? email, bool? verified = true)
    {
        List<Claim> claims = [new("sub", sub, ClaimValueTypes.String, Issuer)];
        if (email is not null) claims.Add(new Claim("email", email));
        if (verified is not null) claims.Add(new Claim("email_verified", verified.Value ? "true" : "false"));
        return new ClaimsPrincipal(new ClaimsIdentity(claims, "oidc"));
    }

    [Fact]
    public void UnknownIdentity_IsRefused_WithoutAutoProvision()
    {
        var (user, problem) = OidcSignIn.Resolve(_accounts.Store, new OidcOptions(), Identity("s1", "eve@example.com"), null);

        Assert.Null(user);
        Assert.Contains("No account is linked", problem);
    }

    [Fact]
    public void AllowedDomain_GetsAnAccount_OnlyWithAVerifiedEmail()
    {
        var config = new OidcOptions { AutoProvision = true, AllowedEmailDomains = ["example.com"] };

        Assert.Null(OidcSignIn.Resolve(_accounts.Store, config, Identity("s1", "eve@example.com", verified: false), null).User);
        Assert.Null(OidcSignIn.Resolve(_accounts.Store, config, Identity("s2", "eve@evil.com"), null).User);

        var (user, _) = OidcSignIn.Resolve(_accounts.Store, config, Identity("s3", "Eve@Example.com"), null);
        Assert.Equal("eve", user!.UserName);
        Assert.False(user.IsAdmin);

        var (again, _) = OidcSignIn.Resolve(_accounts.Store, config, Identity("s3", "eve@example.com"), null);
        Assert.Same(user, again);
    }

    [Fact]
    public void Link_AttachesTheIdentityToTheSignedInAccount_Once()
    {
        var bob = _accounts.Add("bob");

        var (linked, _) = OidcSignIn.Resolve(_accounts.Store, new OidcOptions(), Identity("s1", "bob@example.com"), bob.Id);
        Assert.Same(bob, linked);
        Assert.Same(bob, OidcSignIn.Resolve(_accounts.Store, new OidcOptions(), Identity("s1", null), null).User);

        var carol = _accounts.Add("carol");
        var (stolen, problem) = OidcSignIn.Resolve(_accounts.Store, new OidcOptions(), Identity("s1", null), carol.Id);
        Assert.Null(stolen);
        Assert.Contains("already linked", problem);
    }

    [Fact]
    public void DisabledAccount_IsRefused()
    {
        var bob = _accounts.Add("bob");
        OidcSignIn.Resolve(_accounts.Store, new OidcOptions(), Identity("s1", null), bob.Id);
        _accounts.Store.Update(bob.Id, u => u.Disabled = true);

        Assert.Contains("disabled", OidcSignIn.Resolve(_accounts.Store, new OidcOptions(), Identity("s1", null), null).Problem);
    }

    [Theory]
    [InlineData("/sessions", "/sessions")]
    [InlineData("//evil.com", "/")]
    [InlineData("/\\evil.com", "/")]
    [InlineData("https://evil.com", "/")]
    [InlineData(null, "/")]
    public void ReturnUrls_StayOnSite(string? returnUrl, string expected)
    {
        Assert.Equal(expected, AccountEndpoints.SafeReturn(returnUrl));
    }
}
