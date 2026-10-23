using CesiumNet.Blazor;
using CesiumNet.Gallery.Blazor;
using CesiumNet.Gallery.Blazor.Components;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddRazorComponents().AddInteractiveServerComponents();
builder.Services.AddCesiumNet(o => o.IonAccessToken = builder.Configuration["Cesium:IonToken"]);
builder.Services.AddScoped<GalleryState>();

var app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/error", createScopeForErrors: true);
}

app.UseAntiforgery();
app.MapStaticAssets();
app.MapRazorComponents<App>().AddInteractiveServerRenderMode();

app.Run();
