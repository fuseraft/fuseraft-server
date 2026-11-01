using System.Net;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.Options;
using fuseraft.Web.Components;
using fuseraft.Web.Services;
using fuseraft.Web.Services.Cli;
using fuseraft.Web.Services.Daemons;
using fuseraft.Web.Services.Relay;
using fuseraft.Web.Services.Users;

if (args is ["users", .. var userArgs])
    return UsersCommand.Run(userArgs);
if (args is ["relay", .. var relayArgs])
    return await RelayHost.RunAsync(relayArgs);

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

builder.Services.Configure<FuseraftCliOptions>(builder.Configuration.GetSection("Fuseraft"));
builder.Services.Configure<WebOptions>(builder.Configuration.GetSection("Fuseraft"));
builder.Services.Configure<AuthOptions>(builder.Configuration.GetSection("Auth"));

var web  = builder.Configuration.GetSection("Fuseraft").Get<WebOptions>() ?? new WebOptions();
var auth = builder.Configuration.GetSection("Auth").Get<AuthOptions>() ?? new AuthOptions();

// Holds the accounts and the keys that sign their cookies, so only the server's OS user may read it.
Directory.CreateDirectory(web.ResolvedDataDirectory);
if (!OperatingSystem.IsWindows())
    File.SetUnixFileMode(web.ResolvedDataDirectory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

// Persisted so sign-ins survive a restart.
var keysDirectory  = Path.Combine(web.ResolvedDataDirectory, "keys");
var dataProtection = builder.Services.AddDataProtection()
    .SetApplicationName("fuseraft-web")
    .PersistKeysToFileSystem(new DirectoryInfo(keysDirectory));
if (!string.IsNullOrWhiteSpace(web.KeyCertificate))
{
    var keyCertificate = WebOptions.LoadKeyCertificate(web.KeyCertificate,
        Environment.GetEnvironmentVariable(WebOptions.KeyCertificatePasswordVariable));
    // Keys aren't looked up in a certificate store on Linux, so name the certificate for decrypting too.
    dataProtection.ProtectKeysWithCertificate(keyCertificate).UnprotectKeysWithAnyCertificate(keyCertificate);
}
else if (OperatingSystem.IsWindows())
    dataProtection.ProtectKeysWithDpapi();

var authentication = builder.Services
    .AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(o =>
    {
        o.Cookie.Name         = "fuseraft-web";
        o.Cookie.HttpOnly     = true;
        o.Cookie.SameSite     = SameSiteMode.Lax;
        o.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
        o.ExpireTimeSpan      = TimeSpan.FromHours(auth.SessionHours);
        o.SlidingExpiration   = true;
        o.LoginPath           = "/login";
        o.AccessDeniedPath    = "/access-denied";
        o.Events.OnValidatePrincipal = UserClaims.ValidateAsync;
    });
if (auth.Oidc.Enabled)
    authentication.AddOpenIdConnect(OpenIdConnectDefaults.AuthenticationScheme, o => OidcSignIn.Configure(o, auth.Oidc));

builder.Services.AddAuthorization(o =>
    o.FallbackPolicy = new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build());
builder.Services.AddCascadingAuthenticationState();
builder.Services.AddHttpContextAccessor();

builder.Services.AddRateLimiter(o =>
{
    o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    o.AddPolicy("sign-in", http => RateLimitPartition.GetFixedWindowLimiter(
        http.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = 10, Window = TimeSpan.FromMinutes(1) }));
});

builder.Services.AddSingleton<UserStore>();
builder.Services.AddScoped<CurrentUser>();
builder.Services.AddScoped<WorkspaceAccess>();

builder.Services.AddSingleton<FuseraftCli>();
builder.Services.AddSingleton<HitlBroker>();
builder.Services.AddSingleton<SessionHostService>();
builder.Services.AddSingleton<PluginCatalogService>();
builder.Services.AddSingleton<ModelProfileRegistry>();
builder.Services.AddSingleton<ReplService>();
builder.Services.AddSingleton(sp => new DaemonStore(Path.Combine(web.ResolvedDataDirectory, "daemons.json"),
    sp.GetRequiredService<IDataProtectionProvider>(), sp.GetRequiredService<ILogger<DaemonStore>>()));
builder.Services.AddSingleton<DaemonService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<DaemonService>());
builder.Services.AddHostedService<ScheduleTicker>();
builder.Services.AddHostedService<RelayAdopter>();

// These act on one user's fuseraft home, so each circuit gets its own, bound to the signed-in account.
builder.Services.AddScoped(sp => new ContextService(UserCli(sp), sp.GetRequiredService<WorkspaceAccess>()));
builder.Services.AddScoped(sp => new MemoryService(UserCli(sp)));
builder.Services.AddScoped(sp => new ScheduleService(UserCli(sp), sp.GetRequiredService<WorkspaceAccess>()));
builder.Services.AddScoped(sp => new ConfigService(UserCli(sp), sp.GetRequiredService<WorkspaceAccess>()));
builder.Services.AddScoped(sp => new OrchestrationTemplateService(UserCli(sp), sp.GetRequiredService<WorkspaceAccess>()));
builder.Services.AddScoped(sp => sp.GetRequiredService<ModelProfileRegistry>().For(sp.GetRequiredService<CurrentUser>().User));
builder.Services.AddScoped<WorkspaceService>();

var app = builder.Build();
if (string.IsNullOrWhiteSpace(web.KeyCertificate) && !OperatingSystem.IsWindows())
    app.Logger.LogInformation("The data-protection keys in {Keys} aren't encrypted, so anyone who can read them can forge sign-ins " +
        "and decrypt saved daemon tokens; the directory is readable only by this user. Set Fuseraft:KeyCertificate to encrypt them.",
        keysDirectory);

if (!app.Environment.IsDevelopment())
    app.UseHsts();

app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();
app.UseAntiforgery();

app.MapStaticAssets().AllowAnonymous();
app.MapAccountEndpoints();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Lifetime.ApplicationStarted.Register(() => AnnounceStartup(app));

app.Run();
return 0;

static FuseraftCli UserCli(IServiceProvider sp) =>
    sp.GetRequiredService<FuseraftCli>().ForHome(sp.GetRequiredService<CurrentUser>().User.Home);

static void AnnounceStartup(WebApplication app)
{
    var log       = app.Services.GetRequiredService<ILogger<UserStore>>();
    var users     = app.Services.GetRequiredService<UserStore>();
    var addresses = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()?.Addresses ?? [];

    foreach (var address in addresses)
        if (address.StartsWith("http://") && !IsLoopback(address))
            log.LogWarning(
                "Serving plain HTTP on {Address}. Sign-in cookies and passwords cross the network unencrypted; " +
                "serve HTTPS or put fuseraft-web behind a TLS reverse proxy.", address);

    if (users.SetupCode is { } code)
    {
        var first = addresses.FirstOrDefault()?.Replace("://0.0.0.0", "://localhost").Replace("://[::]", "://localhost")
                    ?? "http://localhost:5000";
        log.LogWarning(
            "There are no accounts yet. Create the first admin at {Url}/setup?code={Code} " +
            "or with 'fuseraft-web users add <name> --admin'.", first.TrimEnd('/'), code);
    }

    if (app.Services.GetRequiredService<IOptions<AuthOptions>>().Value is { LocalAccounts: false, Oidc.Enabled: false })
        log.LogError("Auth:LocalAccounts is off and no OIDC provider is configured, so no one can sign in.");
}

static bool IsLoopback(string address)
{
    var host = new Uri(address.Replace("://+", "://0.0.0.0").Replace("://*", "://0.0.0.0")).Host.Trim('[', ']');
    return host == "localhost" || (IPAddress.TryParse(host, out var ip) && IPAddress.IsLoopback(ip));
}
