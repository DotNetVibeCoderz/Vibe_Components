# Providers and configuration

*Dibuat oleh Gravicode Studios dipimpin oleh Kang Fadhil.* · [Bahasa Indonesia](../id/providers.md)

Blazor.AI talks to models through `Microsoft.Extensions.AI` (`IChatClient`, `IEmbeddingGenerator`). You can register any number of named providers; components use the default unless you pass `Provider="name"`.

## Two ways to register

**Configuration** (recommended: keys stay out of code):

```csharp
builder.Services.AddBlazorAI(builder.Configuration);
```

```json
{
  "BlazorAI": {
    "DefaultProvider": "azure",
    "OfflineEmbeddingDimensions": 384,
    "Providers": [
      { "Name": "simulated", "Kind": "Simulated", "DisplayName": "Simulated (offline)" },
      { "Name": "azure", "Kind": "AzureOpenAI", "Endpoint": "https://contoso.openai.azure.com/", "Model": "gpt-5-mini", "EmbeddingModel": "text-embedding-3-small" },
      { "Name": "claude", "Kind": "Anthropic", "Model": "claude-sonnet-5-5", "MaxOutputTokens": 4096, "Settings": { "workspaceId": "" } }
    ],
    "Guardrails": { "RedactPii": true, "ModerateContent": true, "Audit": true, "EnforceRbac": false }
  }
}
```

Keys come from user-secrets or environment variables, e.g. `BlazorAI__Providers__1__ApiKey`. A cloud provider with no key is skipped at startup instead of failing.

**Code** (fluent builder):

```csharp
builder.Services.AddBlazorAI()
    .AddSimulated()
    .AddAzureOpenAI(endpoint, key, deployment: "gpt-5-mini")
    .AddAnthropic(key, "claude-sonnet-5-5")
    .AddOllama("llama3.2")
    .UseDefault("azure")
    .UseGuardrails(g => g.RedactPii = true);
```

## Supported providers

| Kind | Builder method | Notes |
|---|---|---|
| `Simulated` | `AddSimulated()` | Offline heuristic model with tool calling; used in demos, tests and as the fallback |
| `OpenAI` | `AddOpenAI(key, model, embeddingModel)` | Default model `gpt-5-mini` |
| `AzureOpenAI` | `AddAzureOpenAI(endpoint, key, deployment)` | Model = deployment name |
| `Anthropic` | `AddAnthropic(key, model, maxOutputTokens, workspaceId)` | Default `claude-sonnet-5-5`; embeddings fall back to the offline generator |
| `Gemini` | `AddGemini(key, model)` | OpenAI-compatible endpoint |
| `Mistral` | `AddMistral(key, model)` | |
| `Cohere` | `AddCohere(key, model)` | |
| `DeepSeek` | `AddDeepSeek(key, model)` | |
| `Grok` | `AddGrok(key, model)` | |
| `HuggingFace` | `AddHuggingFace(token, model)` | Hosted inference router |
| `Ollama` | `AddOllama(model, embeddingModel, endpoint)` | Local |
| `LlamaCpp` | `AddLlamaCpp(endpoint, model)` | Local `llama-server` |
| `LocalGen` | `AddLocalGen(endpoint, model)` | Any local OpenAI-compatible server |
| `LocalTransformers` | `AddLocalTransformers(chatModel, embeddingModel)` | In-process Hugging Face models (`Blazor.AI.LocalModels`) |
| `OnnxRuntime` | `AddOnnxEmbeddings(modelPath, tokenizerPath)` | Local ONNX embeddings |
| `Custom` | `AddOpenAICompatible(...)`, `AddChatClient(name, factory)` | Bring your own client |

Reasoning models that reject `temperature` (gpt-5, o-series) are handled automatically: the parameter is dropped and the call retried.

## Choosing a provider at runtime

```razor
<AiProviderPicker />                                   @* top-bar model switcher *@
<SmartSummarizer Provider="claude" />                  @* pin one component *@
```

`IAiClientFactory` gives code access to the same clients:

```csharp
@inject IAiClientFactory Ai
var chat = Ai.GetChatClient("azure");
var embeddings = Ai.GetEmbeddingGenerator();
```

## Local Hugging Face models

```csharp
builder.Services.AddBlazorAI(builder.Configuration)
    .AddLocalTransformers(name: "local-hf");           // downloads models on first use
builder.Services.AddBlazorAILocalModels(o => o.HuggingFaceToken = builder.Configuration["HuggingFace:Token"]);
```

`EngineSelector` switches each Hugging Face component between local inference and the hosted API.

## TypeSafe decisions

`Blazor.AI.Decision` uses TypeSafe System One for judgement calls. Without a key it runs a local simulator.

```csharp
builder.Services.AddBlazorAIDecision(o => o.ApiKey = builder.Configuration["TypeSafe:ApiKey"]);
```
