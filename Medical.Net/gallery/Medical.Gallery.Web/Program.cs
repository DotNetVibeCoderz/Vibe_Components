using Medical.Gallery.Web;
using Medical.Gallery.Web.Components;

// Medical.Net Gallery (web) — Made by Gravicode Studios, led by Kang Fadhil.
var builder = WebApplication.CreateBuilder(args);
builder.Services.AddRazorComponents().AddInteractiveServerComponents();
builder.Services.AddScoped<UiState>();
var app = builder.Build();
if (!app.Environment.IsDevelopment()) app.UseExceptionHandler("/error");
app.UseStaticFiles();
app.UseAntiforgery();
// Isosurface meshes for the 3D demo: int32 vertex count, int32 index count, float32 positions, float32 normals, uint32 indices.
app.MapGet("/mesh/{name}.bin", (string name) =>
{
    var v = Medical.Gallery.Shared.Scenarios.Volume3D();
    var m = name switch { "skull" => v.Skull, "lesion" => v.Lesion, _ => null };
    if (m is null) return Results.NotFound();
    var bytes = new byte[8 + m.Positions.Length * 4 * 2 + m.Indices.Length * 4];
    BitConverter.TryWriteBytes(bytes.AsSpan(0), m.Positions.Length / 3);
    BitConverter.TryWriteBytes(bytes.AsSpan(4), m.Indices.Length);
    Buffer.BlockCopy(m.Positions, 0, bytes, 8, m.Positions.Length * 4);
    Buffer.BlockCopy(m.Normals, 0, bytes, 8 + m.Positions.Length * 4, m.Normals.Length * 4);
    Buffer.BlockCopy(m.Indices, 0, bytes, 8 + m.Positions.Length * 8, m.Indices.Length * 4);
    return Results.Bytes(bytes, "application/octet-stream");
});
app.MapGet("/download/skull.stl", () =>
    Results.File(Medical.Dicom.IsoSurface.Extract(Medical.Dicom.DicomVolume.FromSeries(Medical.Dicom.DicomPhantom.CreateSeries(Medical.Dicom.PhantomKind.Head, 24, 256)), 300, 2).ToStl("Medical.Net skull"),
        "model/stl", "medical-net-skull.stl"));
app.MapRazorComponents<App>().AddInteractiveServerRenderMode();
app.Run();
