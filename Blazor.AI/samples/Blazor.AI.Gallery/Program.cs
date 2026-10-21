using Blazor.AI.Agents;
using Blazor.AI.Gallery.Components;
using Blazor.AI.Gallery.Services;
using Microsoft.Extensions.AI;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents(o => o.DetailedErrors = builder.Environment.IsDevelopment());

// Providers come from configuration (appsettings + user-secrets + environment). The simulated provider
// is always available, so every page works without keys. See tools/Import-DevKeys.ps1 for local keys.
builder.Services.AddBlazorAI(builder.Configuration)
    .AddSimulated()
    .AddLocalTransformers(name: "local-hf");

builder.Services.AddBlazorAIComponents();
builder.Services.AddBlazorAIRag();
builder.Services.AddBlazorAIAgents(tools =>
{
    // App-specific tools are plain .NET methods.
    tools.Register(GalleryTools.LookupOrder, "lookup_order", "Looks up the status of a customer order by its number.", "Demo shop");
    tools.Register(GalleryTools.ExchangeRate, "exchange_rate", "Returns an indicative exchange rate between two currencies (ISO codes).", "Finance");
});
builder.Services.AddBlazorAIDecision(o => o.ApiKey = builder.Configuration["TypeSafe:ApiKey"]);
builder.Services.AddBlazorAIAnalytics();
builder.Services.AddBlazorAILocalModels(o => o.HuggingFaceToken = builder.Configuration["HuggingFace:Token"]);
builder.Services.AddBlazorAIBio();
builder.Services.AddBlazorAIThreeD();
builder.Services.AddSingleton<DemoData>();

var app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/error", createScopeForErrors: true);
    app.UseHsts();
}

app.UseAntiforgery();
app.MapStaticAssets();
app.MapRazorComponents<App>().AddInteractiveServerRenderMode();

app.Run();
