# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## What this is

Blazor.AI is a .NET 10 Razor component library of AI components (smart forms, chat/copilot, RAG, agents, decision intelligence, ML/data science/RL, Hugging Face, visual analytics, biology), shipped as 12 NuGet packages (including the Three.js-based `Blazor.AI.ThreeD`), plus a gallery app, a WASM sample and a `dotnet new` template. `solution-design.md` and `Blazor.AI.txt` (Indonesian) are the original brief; `PLAN.md` is the roadmap and `Progress.md` tracks status. Keep `Progress.md` updated as work proceeds.

## Commands

```bash
dotnet build Blazor.AI.slnx
dotnet test tests/Blazor.AI.Tests
dotnet test tests/Blazor.AI.Tests --filter "FullyQualifiedName~DecisionTests.Loan_table_first_hit"
dotnet run --project samples/Blazor.AI.Gallery          # http://localhost:5180 (Interactive Server)
dotnet run --project samples/Blazor.AI.Wasm             # standalone WebAssembly sample
dotnet pack Blazor.AI.slnx -c Release -o artifacts      # all src/ projects are packable
```

The running gallery locks `bin/` DLLs: stop it before rebuilding. Real provider keys for the gallery come from user-secrets; `tools/Import-DevKeys.ps1` reads the local key files listed below and sets them. Never copy key values into the repo.

## Architecture

- **Central package management**: versions live in `Directory.Packages.props`; `Directory.Build.props` sets net10.0, nullable, and NuGet metadata/icon/README for every project whose path contains `src`. Project files have no versions.
- **Layering**: `Abstractions` (contracts) → `Core` (provider factory, simulated model, text services, vectors, guardrails, DI) → `Components` (theme, JS interop, `AiComponentBase`, primitives) → feature packages (`Visualization`, `RAG`, `Agents`, `Decision`, `Analytics`, `LocalModels`, `Bio`). RAG/Decision/Bio reference Visualization; Bio references Agents (its tools register into `ToolRegistry`).
- **Providers**: `AddBlazorAI(configuration)` binds the `BlazorAI` section manually (cloud providers missing an `ApiKey` are skipped) and returns `BlazorAIBuilder` (`AddOpenAI`, `AddAnthropic`, `AddOllama`, … `UseDefault`, `UseGuardrails`). `AiClientFactory` creates clients lazily and wraps each in `CompatibilityChatClient` (drops temperature for reasoning models) and `GuardrailChatClient` (PII, moderation, RBAC, audit).
- **Simulated model**: `Core/Simulated` answers offline with task-specific heuristics. Callers tag requests with `AiTasks` via `ChatOptions.AdditionalProperties["blazorai.task"]` and wrap inputs with `PromptTags` (`<input>`, `<labels>`, `<fields>`, `<schema>`, …), which the simulator parses. Keep these tags when adding new AI services so offline mode keeps working.
- **Components**: AI components inherit `AiComponentBase` (injects `IAiClientFactory`, `AiTextService`, `BlazorAIJs`; `Provider` parameter; `RunAsync`, `Error`, `IsBusy`, throttled render). Bio components inherit `BioComponentBase` (Biotech.Net work in `AnalyseAsync`, AI only explains computed results). Analytics components inherit `AnalyticsComponentBase`.
- **Deterministic compute**: ML/statistics use Gravicode.Science (GraviNum/GraviFrame/GraviLearn/GraviProb), RL uses Gravicode.RLNet, decisions use TypeSafe SDK and the in-house safe `Expression` language, biology uses Biotech.Net. The LLM plans (e.g. `SpreadsheetAnalyst`) or explains; it does not produce numbers.
- **Theme**: `src/Blazor.AI.Components/wwwroot/blazor-ai.css` ("kawung glass": tarum indigo, soga amber for AI, nila teal for local/success; `--bai-*` tokens; light/dark via `data-bs-theme`). Charts are hand-written SVG in Visualization (`ChartMath`, `LinearAxis`).
- **3D**: `src/Blazor.AI.ThreeD` — `wwwroot/blazor-ai-3d.js` is the only JS engine (vendored Three.js r170 in `wwwroot/lib/three`, imports rewritten to relative paths). C# sends `Object3D[]` JSON (source-generated `ThreeDJsonContext`, nulls dropped); the engine diffs by Id. Components inherit `ThreeDComponentBase` (or `TwinComponentBase` for live twins with a timer). Assets live in `wwwroot/models` (GLB, Draco, WebP) and `wwwroot/art`; `ThreeDAssets` has their URLs; regenerate via `tools/blender`.
- **Gallery**: `samples/Blazor.AI.Gallery`; each page uses `DemoSection` (Preview / Code tabs) with a `const string …Code` snippet per component; navigation and component counts are in `Services/GalleryNavigation.cs`.

## Gotchas

- In Razor code blocks write SVG `<text>` as `<!text>`.
- `[GeneratedRegex]` cannot live in .razor files; attribute values cannot contain inner `"` (move lambdas to methods).
- A component that shares a name with a library type (`PrimerDesigner`, `CodonOptimizer`) shadows it inside its package: fully qualify the library type.
- Interpolated-string concatenations passed to `string.Create(CultureInfo, …)` must all be `$"…"` parts.
- `AiComponentBase` implements `IAsyncDisposable`; override `DisposeAsync` instead of adding `IDisposable`.
- GraviNum `NdArray.FromArray` takes `double[,]`; use `FromValues` for vectors. `Math.Round` has no negative digits (use `RoundTo`).
- SixLabors.ImageSharp is pinned to 3.1.12 (4.x needs a licence key in Release); its advisories are suppressed via `NuGetAuditSuppress` and documented in `docs/en/security.md`.

## Requirements from the brief

- Docs in **English and Bahasa Indonesia** (`docs/en`, `docs/id`, bilingual README) with screenshots (`docs/screenshots`).
- Show "Dibuat oleh Gravicode Studios dipimpin oleh Kang Fadhil" in docs and apps (`AiCredit` component).
- Polished UI for samples; use the `frontend-design` skill for new UI.

### Credentials (read at runtime; never commit values)

| Purpose | File |
|---|---|
| NuGet publish API key | `C:\Users\mifma\Documents\CodeSandbox\PackageCredentials.txt` |
| Real LLM testing | `C:\Users\mifma\Documents\CodeSandbox\testkey.txt` |
| Hugging Face token | `C:\Users\mifma\Documents\CodeSandbox\HFToken.txt` |
| TypeSafe decision API | `C:\Users\mifma\Documents\CodeSandbox\TypeSafeApiKey.txt` |

### Publishing

The solution is published from the monorepo `C:\experiment\VibeCoding\Vibe_Components` (subfolder `Blazor.AI`). The workflow `.github/workflows/blazor-ai.yml` there publishes on tags `blazor-ai-v*` using the `NUGET_API_KEY` repository secret. Confirm with the owner before pushing or publishing.
