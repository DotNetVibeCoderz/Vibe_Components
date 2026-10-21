# Memulai

*Dibuat oleh Gravicode Studios dipimpin oleh Kang Fadhil.* · [English](../en/getting-started.md)

Panduan ini membawa kamu dari folder kosong sampai punya aplikasi Blazor dengan AI chat dan SmartPaste, lalu menunjukkan cara menambahkan package lainnya.

## Prasyarat

- .NET 10 SDK
- Blazor Web App dengan render mode interactive server atau WebAssembly. Package-package ini berupa Razor class library, jadi bisa dipakai di keduanya.
- Opsional: API key untuk model cloud, atau server Ollama, llama.cpp, atau LocalGen lokal. Tanpa itu, setiap component memakai model simulasi bawaan.

## 1. Buat aplikasi

Cara tercepat adalah lewat template:

```bash
dotnet new install Blazor.AI.Templates
dotnet new blazorai -n MyAiApp                 # model simulasi, tanpa key
dotnet new blazorai -n MyAiApp --provider azure --agents true
cd MyAiApp && dotnet run
```

| Opsi template | Nilai | Efek |
|---|---|---|
| `--provider` | `simulated` (default), `openai`, `azure`, `anthropic`, `ollama` | Menambahkan provider ke `appsettings.json` dan menjadikannya default |
| `--agents` | `true` / `false` | Menambahkan `Blazor.AI.Agents` dan halaman `/agent` dengan contoh tool |

## 2. Atau tambahkan Blazor.AI ke aplikasi yang sudah ada

```bash
dotnet add package Blazor.AI.Components
```

Daftarkan service di `Program.cs`:

```csharp
builder.Services.AddBlazorAI(builder.Configuration)   // membaca section "BlazorAI"
    .AddSimulated();                                  // model offline, selalu tersedia
builder.Services.AddBlazorAIComponents();
```

Tambahkan theme ke `App.razor`. Theme ini butuh Bootstrap Icons; Bootstrap sendiri opsional.

```html
<link href="https://fonts.googleapis.com/css2?family=Bricolage+Grotesque:opsz,wdth,wght@12..96,75..100,400..800&family=JetBrains+Mono:wght@400;500&family=Plus+Jakarta+Sans:wght@400;500;600;700&display=swap" rel="stylesheet" />
<link href="https://cdn.jsdelivr.net/npm/bootstrap-icons@1.11.3/font/bootstrap-icons.min.css" rel="stylesheet" />
<link rel="stylesheet" href="@Assets["_content/Blazor.AI.Components/blazor-ai.css"]" />
...
<body class="bai-surface">
```

Tambahkan `@using Blazor.AI.Components` ke `_Imports.razor`.

## 3. Component pertamamu

```razor
@page "/"

<form data-smartpaste-scope>
    <label>Name <input name="name" @bind="name" /></label>
    <label>Email <input name="email" type="email" @bind="email" /></label>
    <SmartPaste />
</form>

<AiChat Title="Assistant" SystemPrompt="You answer questions about our product." Suggestions="@(["What can you do?"])" />

@code { string? name, email; }
```

SmartPaste membaca clipboard, meminta model memetakan isinya ke field di form terdekat (`data-smartpaste-scope`), lalu mengisinya. Nama field, label, dan opsi `<select>` dikirim sebagai petunjuk. Form tidak akan di-submit otomatis.

## 4. Pilih model sungguhan

Tambahkan provider di `appsettings.json` dan simpan key-nya di user-secrets:

```json
{
  "BlazorAI": {
    "DefaultProvider": "openai",
    "Providers": [
      { "Name": "simulated", "Kind": "Simulated" },
      { "Name": "openai", "Kind": "OpenAI", "Model": "gpt-5-mini" }
    ]
  }
}
```

```bash
dotnet user-secrets init
dotnet user-secrets set "BlazorAI:Providers:1:ApiKey" "sk-..."
```

`<AiProviderPicker />` memungkinkan user berganti model saat runtime, dan setiap component juga punya parameter `Provider`. Lihat [providers](providers.md) untuk daftar semua provider beserta pengaturannya.

## 5. Tambahkan package lain

| Kebutuhan | Package | Registrasi |
|---|---|---|
| Chart dan dashboard | `Blazor.AI.Visualization` | tidak perlu (component murni) |
| Chat dengan dokumenmu | `Blazor.AI.RAG` | `AddBlazorAIRag()` |
| Agent dengan tool calling, MCP | `Blazor.AI.Agents` | `AddBlazorAIAgents(tools => tools.Register(...))` |
| Rule, decision table, risiko | `Blazor.AI.Decision` | `AddBlazorAIDecision(o => o.ApiKey = ...)` |
| ML, statistik, RL | `Blazor.AI.Analytics` | `AddBlazorAIAnalytics()` |
| Hugging Face dan model lokal | `Blazor.AI.LocalModels` | `AddBlazorAILocalModels(o => o.HuggingFaceToken = ...)` |
| Biologi dan kimia | `Blazor.AI.Bio` | `AddBlazorAIAgents().AddBlazorAIBio()` |

[Katalog component](components.md) berisi daftar semua component lengkap dengan contohnya.

## 6. Jalankan gallery

Aplikasi gallery di repository ini menampilkan ke-109 component dengan live preview dan kodenya:

```bash
git clone https://github.com/DotNetVibeCoderz/Vibe_Components
cd Vibe_Components/Blazor.AI
dotnet run --project samples/Blazor.AI.Gallery      # http://localhost:5180
```

![Gallery home](../screenshots/home.jpg)

Untuk mencoba model sungguhan, set key dengan `dotnet user-secrets` di `samples/Blazor.AI.Gallery` (lihat `tools/Import-DevKeys.ps1` untuk nama-nama key yang diharapkan).
