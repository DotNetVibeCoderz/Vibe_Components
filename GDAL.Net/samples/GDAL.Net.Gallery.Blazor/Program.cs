using GDAL.Net;
using GDAL.Net.Gallery;
using GDAL.Net.Gallery.Blazor.Components;
using GDAL.Net.Gallery.Blazor.Services;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents(o => o.DetailedErrors = builder.Environment.IsDevelopment());

// Raster PNGs travel over the circuit: allow larger messages than the 32 KB default.
builder.Services.AddSignalR(o => o.MaximumReceiveMessageSize = 1024 * 1024);

builder.Services.AddSingleton(new SampleContext(Path.Combine(AppContext.BaseDirectory, "gallery-data")));
builder.Services.AddSingleton<GalleryData>();
builder.Services.AddScoped<UiState>();

var app = builder.Build();

// Start generating sample data immediately (the UI shows progress while it runs).
_ = app.Services.GetRequiredService<GalleryData>();
app.Logger.LogInformation("GDAL {Version}, PROJ {Proj}, kernels: {Kernels}", Gdal.Version, Gdal.ProjVersion, GDAL.Net.Processing.Kernels.Backend);

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
}
app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
app.UseAntiforgery();

app.MapStaticAssets();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();
