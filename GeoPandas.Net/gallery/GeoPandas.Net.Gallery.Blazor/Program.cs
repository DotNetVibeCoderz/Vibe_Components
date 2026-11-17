using GeoPandas.Net.Gallery.Blazor.Components;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddRazorComponents().AddInteractiveServerComponents();

var app = builder.Build();
if (!app.Environment.IsDevelopment()) app.UseExceptionHandler("/Error", createScopeForErrors: true);
app.UseAntiforgery();
app.MapStaticAssets();
app.MapRazorComponents<App>().AddInteractiveServerRenderMode();

// Warm the sample cache in the background so the first page view is instant.
_ = Task.Run(() => { foreach (var s in GeoPandas.Net.Gallery.GalleryCatalog.Samples) s.Run(); });

app.Run();
