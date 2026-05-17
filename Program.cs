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

var app = builder.Build();

if (!app.Environment.IsDevelopment())
    app.UseHsts();

app.UseStaticFiles();
app.UseAntiforgery();

app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();
