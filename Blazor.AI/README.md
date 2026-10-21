<p align="center">
  <img src="https://raw.githubusercontent.com/DotNetVibeCoderz/Vibe_Components/main/Blazor.AI/assets/icon-512.png" width="112" alt="Blazor.AI logo: a kawung batik motif" />
</p>

<h1 align="center">Blazor.AI</h1>

<p align="center">
  <b>AI components for Blazor</b>: smart forms, copilots, cited document chat, agents, decision engines,
  machine learning, reinforcement learning, Hugging Face models and biology workbenches.<br/>
  .NET 10 · Microsoft.Extensions.AI · works offline without keys
</p>

<p align="center"><i>Dibuat oleh Gravicode Studios dipimpin oleh Kang Fadhil.</i></p>

<p align="center"><a href="#english">English</a> · <a href="#bahasa-indonesia">Bahasa Indonesia</a></p>

![Blazor.AI gallery home: paste an email, get a filled form](https://raw.githubusercontent.com/DotNetVibeCoderz/Vibe_Components/main/Blazor.AI/docs/screenshots/home.jpg)

---

## English

### Why Blazor.AI

- **109 ready-made components** across 11 NuGet packages, all styled by one theme (light and dark).
- **Any model**: OpenAI, Azure OpenAI, Anthropic Claude, Gemini, Mistral, Cohere, DeepSeek, Grok, Hugging Face, Ollama, llama.cpp, LocalGen, any OpenAI-compatible endpoint, ONNX and in-process Hugging Face transformers. Every component also runs on a built-in **offline simulated model**, so demos and tests need no keys.
- **Computation stays deterministic.** Machine learning, statistics, rules, chemistry and sequence analysis run in .NET (GraviLearn, GraviFrame, RLNet, Biotech.Net). The LLM plans and explains; it does not invent the numbers.
- **Guardrails built in**: PII redaction, content moderation, audit trail, AES-GCM encrypted prompt store and API-key vault, role-based access.

### Packages

| Package | What you get |
|---|---|
| `Blazor.AI.Abstractions` | Provider options, vector, guardrail and smart-component contracts |
| `Blazor.AI.Core` | Provider factory, simulated model, text services, vector index, guardrails, DI (`AddBlazorAI`) |
| `Blazor.AI.Components` | Smart components (SmartPaste, SmartTextArea, SmartComboBox…), AiChat, AiAssistant, AiCopilot, generators, coding and productivity tools, theme |
| `Blazor.AI.Visualization` | SVG charts: line, bar, scatter, heatmap, Sankey, treemap, network and knowledge graphs, geo map, forecast, realtime, AI dashboard |
| `Blazor.AI.RAG` | Document ingestion (PDF, Office, CSV), vector search, cited document chat, PDF and spreadsheet chat, knowledge graph |
| `Blazor.AI.Agents` | Tool-calling agents, multi-agent teams, task board, memory, debugger, MCP explorer, workflow designer |
| `Blazor.AI.Decision` | Rule editor, DMN-style decision tables and trees, policies, risk, eligibility, approvals, dynamic pricing (TypeSafe System One) |
| `Blazor.AI.Analytics` | AutoML, six ML workbenches, statistics, Bayesian A/B, hypothesis tests, PCA, time series, signals, RL studio |
| `Blazor.AI.LocalModels` | Hugging Face hub browser and downloader, local inference: sentiment, NER, embeddings, vision, OCR, speech |
| `Blazor.AI.Bio` | DNA, restriction maps, alignment, phylogeny, proteins, molecules and ADMET, CRISPR, primers, codon optimisation, RNA-seq, bio copilot |
| `Blazor.AI.Templates` | `dotnet new blazorai` project template |

### Quick start

```bash
dotnet new install Blazor.AI.Templates
dotnet new blazorai -n MyAiApp --provider openai --agents true
```

Or add it to an existing Blazor Web App:

```bash
dotnet add package Blazor.AI.Components
```

```csharp
// Program.cs
builder.Services.AddBlazorAI(builder.Configuration)       // providers from the "BlazorAI" section
    .AddSimulated()                                       // offline fallback
    .AddOpenAI(builder.Configuration["OpenAI:Key"]!, "gpt-5-mini");
builder.Services.AddBlazorAIComponents();
```

```html
<!-- App.razor <head> -->
<link rel="stylesheet" href="_content/Blazor.AI.Components/blazor-ai.css" />
<link rel="stylesheet" href="https://cdn.jsdelivr.net/npm/bootstrap-icons@1.11.3/font/bootstrap-icons.min.css" />
```

```razor
<form>
    <input name="name" /> <input name="email" /> <input name="company" />
    <SmartPaste />
</form>

<AiChat Title="Support" SystemPrompt="You help customers of Contoso." />
```

### Configuration

```json
{
  "BlazorAI": {
    "DefaultProvider": "azure",
    "Providers": [
      { "Name": "simulated", "Kind": "Simulated" },
      { "Name": "azure", "Kind": "AzureOpenAI", "Endpoint": "https://<resource>.openai.azure.com/", "Model": "gpt-5-mini" },
      { "Name": "claude", "Kind": "Anthropic", "Model": "claude-sonnet-5-5" },
      { "Name": "local", "Kind": "Ollama", "Endpoint": "http://localhost:11434", "Model": "llama3.2" }
    ],
    "Guardrails": { "RedactPii": true, "ModerateContent": true, "Audit": true }
  }
}
```

Put keys in user-secrets or environment variables (`BlazorAI__Providers__1__ApiKey`), never in `appsettings.json`. Cloud providers without a key are skipped, and the simulated model stays available.

### Gallery

The gallery app shows every component with a live preview and copy-ready code.

```bash
dotnet run --project samples/Blazor.AI.Gallery    # http://localhost:5180
```

| | |
|---|---|
| ![Smart components](https://raw.githubusercontent.com/DotNetVibeCoderz/Vibe_Components/main/Blazor.AI/docs/screenshots/smart.jpg) | ![Agents](https://raw.githubusercontent.com/DotNetVibeCoderz/Vibe_Components/main/Blazor.AI/docs/screenshots/agents.jpg) |
| Smart components | Agents |
| ![RAG](https://raw.githubusercontent.com/DotNetVibeCoderz/Vibe_Components/main/Blazor.AI/docs/screenshots/rag.jpg) | ![Machine learning](https://raw.githubusercontent.com/DotNetVibeCoderz/Vibe_Components/main/Blazor.AI/docs/screenshots/ml.jpg) |
| RAG and knowledge | Machine learning |
| ![Decision intelligence](https://raw.githubusercontent.com/DotNetVibeCoderz/Vibe_Components/main/Blazor.AI/docs/screenshots/decision.jpg) | ![Biology](https://raw.githubusercontent.com/DotNetVibeCoderz/Vibe_Components/main/Blazor.AI/docs/screenshots/bio-dark.jpg) |
| Decision intelligence | Biology (dark theme) |
| ![Visual analytics](https://raw.githubusercontent.com/DotNetVibeCoderz/Vibe_Components/main/Blazor.AI/docs/screenshots/visualization.jpg) | ![Hugging Face](https://raw.githubusercontent.com/DotNetVibeCoderz/Vibe_Components/main/Blazor.AI/docs/screenshots/huggingface.jpg) |
| Visual analytics | Hugging Face |

### Documentation

- [Getting started](https://github.com/DotNetVibeCoderz/Vibe_Components/blob/main/Blazor.AI/docs/en/getting-started.md)
- [Providers and configuration](https://github.com/DotNetVibeCoderz/Vibe_Components/blob/main/Blazor.AI/docs/en/providers.md)
- [Component catalogue](https://github.com/DotNetVibeCoderz/Vibe_Components/blob/main/Blazor.AI/docs/en/components.md)
- [Security and guardrails](https://github.com/DotNetVibeCoderz/Vibe_Components/blob/main/Blazor.AI/docs/en/security.md)

### Build and test

```bash
dotnet build Blazor.AI.slnx
dotnet test tests/Blazor.AI.Tests
dotnet pack Blazor.AI.slnx -c Release -o artifacts
```

Requires the .NET 10 SDK. MIT licensed.

---

## Bahasa Indonesia

### Kenapa Blazor.AI

- **109 komponen siap pakai** dalam 11 paket NuGet, dengan satu tema yang konsisten (terang dan gelap).
- **Model apa saja**: OpenAI, Azure OpenAI, Anthropic Claude, Gemini, Mistral, Cohere, DeepSeek, Grok, Hugging Face, Ollama, llama.cpp, LocalGen, endpoint kompatibel OpenAI, ONNX, dan transformer Hugging Face lokal. Semua komponen juga jalan dengan **model simulasi offline**, jadi demo dan tes tidak butuh API key.
- **Perhitungan tetap deterministik.** Machine learning, statistik, aturan bisnis, kimia, dan analisis sekuens dihitung di .NET (GraviLearn, GraviFrame, RLNet, Biotech.Net). LLM merencanakan dan menjelaskan, bukan mengarang angka.
- **Guardrail bawaan**: redaksi PII, moderasi konten, jejak audit, penyimpanan prompt dan brankas API key terenkripsi AES-GCM, serta kontrol akses berbasis peran.

### Paket

| Paket | Isi |
|---|---|
| `Blazor.AI.Abstractions` | Kontrak provider, vektor, guardrail, dan smart component |
| `Blazor.AI.Core` | Factory provider, model simulasi, layanan teks, indeks vektor, guardrail, DI (`AddBlazorAI`) |
| `Blazor.AI.Components` | Smart component (SmartPaste, SmartTextArea, SmartComboBox…), AiChat, AiAssistant, AiCopilot, generator konten, alat coding dan produktivitas, tema |
| `Blazor.AI.Visualization` | Grafik SVG: garis, batang, sebaran, heatmap, Sankey, treemap, graf jaringan dan pengetahuan, peta, forecast, realtime, dasbor AI |
| `Blazor.AI.RAG` | Ingesti dokumen (PDF, Office, CSV), pencarian vektor, chat dokumen bersitasi, chat PDF dan spreadsheet, knowledge graph |
| `Blazor.AI.Agents` | Agen pemanggil tool, tim multi-agen, papan tugas, memori, debugger, MCP explorer, desainer workflow |
| `Blazor.AI.Decision` | Editor aturan, tabel dan pohon keputusan gaya DMN, kebijakan, risiko, kelayakan, persetujuan, harga dinamis (TypeSafe System One) |
| `Blazor.AI.Analytics` | AutoML, enam workbench ML, statistik, A/B Bayesian, uji hipotesis, PCA, deret waktu, sinyal, studio RL |
| `Blazor.AI.LocalModels` | Browser dan pengunduh Hugging Face Hub, inferensi lokal: sentimen, NER, embedding, visi, OCR, suara |
| `Blazor.AI.Bio` | DNA, peta restriksi, alignment, filogeni, protein, molekul dan ADMET, CRISPR, primer, optimasi kodon, RNA-seq, bio copilot |
| `Blazor.AI.Templates` | Template proyek `dotnet new blazorai` |

### Mulai cepat

```bash
dotnet new install Blazor.AI.Templates
dotnet new blazorai -n AplikasiAI --provider openai --agents true
```

Atau tambahkan ke aplikasi Blazor Web App yang sudah ada:

```bash
dotnet add package Blazor.AI.Components
```

```csharp
// Program.cs
builder.Services.AddBlazorAI(builder.Configuration)       // provider dari bagian "BlazorAI"
    .AddSimulated()                                       // cadangan offline
    .AddOpenAI(builder.Configuration["OpenAI:Key"]!, "gpt-5-mini");
builder.Services.AddBlazorAIComponents();
```

```razor
<form>
    <input name="nama" /> <input name="email" /> <input name="perusahaan" />
    <SmartPaste />
</form>

<AiChat Title="Layanan pelanggan" SystemPrompt="Kamu membantu pelanggan Toko Nusantara. Jawab dalam Bahasa Indonesia." />
```

Simpan API key di user-secrets atau variabel lingkungan (`BlazorAI__Providers__1__ApiKey`), jangan di `appsettings.json`. Provider cloud tanpa key akan dilewati, dan model simulasi tetap tersedia.

### Galeri

Aplikasi galeri menampilkan setiap komponen dengan pratinjau langsung dan contoh kode yang siap disalin.

```bash
dotnet run --project samples/Blazor.AI.Gallery    # http://localhost:5180
```

### Dokumentasi

- [Memulai](https://github.com/DotNetVibeCoderz/Vibe_Components/blob/main/Blazor.AI/docs/id/getting-started.md)
- [Provider dan konfigurasi](https://github.com/DotNetVibeCoderz/Vibe_Components/blob/main/Blazor.AI/docs/id/providers.md)
- [Katalog komponen](https://github.com/DotNetVibeCoderz/Vibe_Components/blob/main/Blazor.AI/docs/id/components.md)
- [Keamanan dan guardrail](https://github.com/DotNetVibeCoderz/Vibe_Components/blob/main/Blazor.AI/docs/id/security.md)

### Build dan tes

```bash
dotnet build Blazor.AI.slnx
dotnet test tests/Blazor.AI.Tests
```

Butuh .NET 10 SDK. Lisensi MIT.

---

<p align="center">Made by <b>Gravicode Studios</b>, led by <b>Kang Fadhil</b> · <i>Dibuat oleh Gravicode Studios dipimpin oleh Kang Fadhil</i></p>
