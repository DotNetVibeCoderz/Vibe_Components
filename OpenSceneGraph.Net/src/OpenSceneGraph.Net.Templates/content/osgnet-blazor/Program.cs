// OpenSceneGraph.Net Blazor app - Created by Gravicode Studios, led by Kang Fadhil.
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using OsgNetBlazorApp;

var builder = WebAssemblyHostBuilder.CreateDefault(args);
builder.RootComponents.Add<App>("#app");
await builder.Build().RunAsync();
