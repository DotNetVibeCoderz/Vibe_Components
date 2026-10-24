using GnuRadio.Gallery.Web;
using GnuRadio.Gallery.Web.Components;

// GnuRadio.Net Gallery (Blazor). Flowgraphs run on the server; the components stream frames to canvases.
// Dibuat oleh Gravicode Studios dipimpin oleh Kang Fadhil.
var builder = WebApplication.CreateBuilder(args);
builder.Services.AddRazorComponents().AddInteractiveServerComponents();
builder.Services.AddScoped<GalleryState>();

var app = builder.Build();
if (!app.Environment.IsDevelopment()) app.UseExceptionHandler("/error", createScopeForErrors: true);
app.UseAntiforgery();
app.MapStaticAssets();
app.MapRazorComponents<App>().AddInteractiveServerRenderMode();
app.Run();
