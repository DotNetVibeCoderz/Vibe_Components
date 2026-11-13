using MedicalWardBoard;

// Medical.Net ward board — Made by Gravicode Studios, led by Kang Fadhil.
var builder = WebApplication.CreateBuilder(args);
builder.Services.AddRazorComponents();
var app = builder.Build();
app.UseStaticFiles();
app.UseAntiforgery();
app.MapRazorComponents<App>();
app.Run();
