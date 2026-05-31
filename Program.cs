using fuseraft.Infrastructure.Plugins;
using fuseraft.Server.Components;
using fuseraft.Server.Services;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

builder.Services.AddSingleton(_ => new PluginRegistry().RegisterDefaults());
builder.Services.AddSingleton<HitlBroker>();
builder.Services.AddSingleton<SessionHostService>();
builder.Services.AddSingleton<ScheduleService>();
builder.Services.AddSingleton<OrchestrationTemplateService>();
builder.Services.AddSingleton<PluginCatalogService>();
builder.Services.AddSingleton<ContextService>();
builder.Services.AddSingleton<ConfigService>();
builder.Services.AddSingleton<WorkspaceService>();
builder.Services.AddSingleton<ModelProfileService>();
builder.Services.AddSingleton<MemoryService>();
builder.Services.AddSingleton<ReplService>();

var app = builder.Build();

if (!app.Environment.IsDevelopment())
    app.UseHsts();

app.UseStaticFiles();
app.UseAntiforgery();

app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();
