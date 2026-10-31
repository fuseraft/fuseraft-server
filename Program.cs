using fuseraft.Server.Components;
using fuseraft.Server.Services;
using fuseraft.Server.Services.Cli;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

builder.Services.Configure<FuseraftCliOptions>(builder.Configuration.GetSection("Fuseraft"));
builder.Services.AddSingleton<FuseraftCli>();
builder.Services.AddSingleton<HitlBroker>();
builder.Services.AddSingleton<SessionHostService>();
builder.Services.AddSingleton<PluginCatalogService>();
builder.Services.AddSingleton<ContextService>();
builder.Services.AddSingleton<WorkspaceService>();
builder.Services.AddSingleton<ModelProfileService>();
builder.Services.AddSingleton<ReplService>();

var app = builder.Build();

if (!app.Environment.IsDevelopment())
    app.UseHsts();

app.UseStaticFiles();
app.UseAntiforgery();

app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();
