using fuseraft.Server.Components;
using fuseraft.Server.Services;
using fuseraft.Server.Services.Cli;
using fuseraft.Server.Services.Daemons;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

builder.Services.Configure<FuseraftCliOptions>(builder.Configuration.GetSection("Fuseraft"));
builder.Services.AddSingleton<FuseraftCli>();
builder.Services.AddSingleton<HitlBroker>();
builder.Services.AddSingleton<SessionHostService>();
builder.Services.AddSingleton<PluginCatalogService>();
builder.Services.AddSingleton<ContextService>();
builder.Services.AddSingleton<MemoryService>();
builder.Services.AddSingleton<ScheduleService>();
builder.Services.AddSingleton<ConfigService>();
builder.Services.AddSingleton<OrchestrationTemplateService>();
builder.Services.AddSingleton<WorkspaceService>();
builder.Services.AddSingleton<ModelProfileService>();
builder.Services.AddSingleton<ReplService>();
builder.Services.AddSingleton<DaemonService>();

var app = builder.Build();

if (!app.Environment.IsDevelopment())
    app.UseHsts();

app.UseStaticFiles();
app.UseAntiforgery();

app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();
