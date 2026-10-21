# Provider dan konfigurasi

*Dibuat oleh Gravicode Studios dipimpin oleh Kang Fadhil.* · [English](../en/providers.md)

Blazor.AI berkomunikasi dengan model lewat `Microsoft.Extensions.AI` (`IChatClient`, `IEmbeddingGenerator`). Kamu bisa mendaftarkan provider bernama sebanyak yang kamu mau; component memakai provider default kecuali kamu memberikan `Provider="name"`.

## Dua cara registrasi

**Konfigurasi** (disarankan: key tidak tercampur di kode):

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

Key diambil dari user-secrets atau environment variable, misalnya `BlazorAI__Providers__1__ApiKey`. Provider cloud yang tidak punya key akan dilewati saat startup, bukan membuat aplikasi gagal.

**Kode** (fluent builder):

```csharp
builder.Services.AddBlazorAI()
    .AddSimulated()
    .AddAzureOpenAI(endpoint, key, deployment: "gpt-5-mini")
    .AddAnthropic(key, "claude-sonnet-5-5")
    .AddOllama("llama3.2")
    .UseDefault("azure")
    .UseGuardrails(g => g.RedactPii = true);
```

## Provider yang didukung

| Kind | Method builder | Catatan |
|---|---|---|
| `Simulated` | `AddSimulated()` | Model heuristik offline dengan tool calling; dipakai untuk demo, test, dan sebagai fallback |
| `OpenAI` | `AddOpenAI(key, model, embeddingModel)` | Model default `gpt-5-mini` |
| `AzureOpenAI` | `AddAzureOpenAI(endpoint, key, deployment)` | Model = nama deployment |
| `Anthropic` | `AddAnthropic(key, model, maxOutputTokens, workspaceId)` | Default `claude-sonnet-5-5`; embedding fallback ke generator offline |
| `Gemini` | `AddGemini(key, model)` | Endpoint yang kompatibel dengan OpenAI |
| `Mistral` | `AddMistral(key, model)` | |
| `Cohere` | `AddCohere(key, model)` | |
| `DeepSeek` | `AddDeepSeek(key, model)` | |
| `Grok` | `AddGrok(key, model)` | |
| `HuggingFace` | `AddHuggingFace(token, model)` | Hosted inference router |
| `Ollama` | `AddOllama(model, embeddingModel, endpoint)` | Lokal |
| `LlamaCpp` | `AddLlamaCpp(endpoint, model)` | `llama-server` lokal |
| `LocalGen` | `AddLocalGen(endpoint, model)` | Server lokal apa pun yang kompatibel dengan OpenAI |
| `LocalTransformers` | `AddLocalTransformers(chatModel, embeddingModel)` | Model Hugging Face in-process (`Blazor.AI.LocalModels`) |
| `OnnxRuntime` | `AddOnnxEmbeddings(modelPath, tokenizerPath)` | Embedding ONNX lokal |
| `Custom` | `AddOpenAICompatible(...)`, `AddChatClient(name, factory)` | Pakai client buatanmu sendiri |

Reasoning model yang menolak `temperature` (gpt-5, seri o) ditangani otomatis: parameter tersebut dihapus lalu panggilan diulang.

## Memilih provider saat runtime

```razor
<AiProviderPicker />                                   @* top-bar model switcher *@
<SmartSummarizer Provider="claude" />                  @* pin one component *@
```

`IAiClientFactory` memberi akses ke client yang sama dari kode:

```csharp
@inject IAiClientFactory Ai
var chat = Ai.GetChatClient("azure");
var embeddings = Ai.GetEmbeddingGenerator();
```

## Model Hugging Face lokal

```csharp
builder.Services.AddBlazorAI(builder.Configuration)
    .AddLocalTransformers(name: "local-hf");           // mengunduh model saat pertama kali dipakai
builder.Services.AddBlazorAILocalModels(o => o.HuggingFaceToken = builder.Configuration["HuggingFace:Token"]);
```

`EngineSelector` mengganti setiap component Hugging Face antara inference lokal dan hosted API.

## Keputusan dengan TypeSafe

`Blazor.AI.Decision` memakai TypeSafe System One untuk pengambilan keputusan (judgement call). Tanpa key, component ini berjalan dengan simulator lokal.

```csharp
builder.Services.AddBlazorAIDecision(o => o.ApiKey = builder.Configuration["TypeSafe:ApiKey"]);
```
