using Electrical.Gallery.Web;
using Electrical.Gallery.Web.Components;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddRazorComponents().AddInteractiveServerComponents();

var app = builder.Build();
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
}
app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
app.UseAntiforgery();
app.MapStaticAssets();
BlobStore.Map(app);
Electrical.Gallery.Shared.LearningDemo.Warm(); // train the cached ML studies in the background
_ = Task.Run(() => { var d = new Electrical.Gallery.Shared.Twin3DDemo(); try { d.Run(Electrical.Gallery.Shared.InteractiveScenarios.Defaults(d), "en"); } catch (InvalidOperationException) { } }); // GPU / scene warm-up
app.MapRazorComponents<App>().AddInteractiveServerRenderMode();
app.Run();
