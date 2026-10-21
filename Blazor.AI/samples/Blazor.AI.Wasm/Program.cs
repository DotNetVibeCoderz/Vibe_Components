using Blazor.AI.Wasm;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;

var builder = WebAssemblyHostBuilder.CreateDefault(args);
builder.RootComponents.Add<App>("#app");
builder.RootComponents.Add<HeadOutlet>("head::after");

// Everything here runs in the browser. Never ship cloud API keys in a WebAssembly app:
// use the simulated model, a local endpoint (Ollama on localhost), or a server-side proxy you control.
var ai = builder.Services.AddBlazorAI(builder.Configuration).AddSimulated();
if (builder.Configuration["Ollama:Model"] is { Length: > 0 } ollamaModel)
{
    ai.AddOllama(ollamaModel, endpoint: builder.Configuration["Ollama:Endpoint"] ?? "http://localhost:11434");
}

builder.Services.AddBlazorAIComponents();
builder.Services.AddBlazorAIDecision();

await builder.Build().RunAsync();
