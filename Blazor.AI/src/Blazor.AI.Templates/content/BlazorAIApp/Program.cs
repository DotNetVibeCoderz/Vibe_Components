using BlazorAIApp.Components;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

// Providers are read from the "BlazorAI" section (appsettings, user-secrets, environment variables).
// Keep keys out of source control: dotnet user-secrets set "BlazorAI:Providers:1:ApiKey" "<key>"
builder.Services.AddBlazorAI(builder.Configuration)
    .AddSimulated();
builder.Services.AddBlazorAIComponents();
//#if (agents)
builder.Services.AddBlazorAIAgents(tools =>
    tools.Register((Func<string>)(() => DateTimeOffset.Now.ToString("f")), "current_time", "Returns the server's current date and time.", "Utilities"));
//#endif

var app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    app.UseHsts();
}

app.UseAntiforgery();
app.MapStaticAssets();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();
