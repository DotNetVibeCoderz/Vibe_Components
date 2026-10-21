# Blazor.AI: progress

_Last updated: 2026-10-21. Dibuat oleh Gravicode Studios dipimpin oleh Kang Fadhil._

Roadmap: [PLAN.md](PLAN.md).

## Status

| Package / item | Status | Notes |
|---|---|---|
| Blazor.AI.Abstractions | ✅ | Providers, vectors, smart contracts, guardrails, prompt store |
| Blazor.AI.Core | ✅ | 16 provider kinds, simulated model, hashing embeddings, SIMD vector index, VectorData adapter, guardrails, reasoning-model compatibility |
| Blazor.AI.Components | ✅ | Kawung glass theme, primitives, 9 smart, 3 conversational, 5 productivity, 5 content, 4 coding |
| Blazor.AI.Visualization | ✅ | 10 visual components + chart primitives |
| Blazor.AI.RAG | ✅ | 9 components; PDF/Office/CSV loaders; citations; knowledge graph |
| Blazor.AI.Agents | ✅ | 9 components; tracing runtime, memory, orchestrator, workflows, MCP |
| Blazor.AI.Decision | ✅ | 8 components; expression language, rules, DMN, trees, TypeSafe |
| Blazor.AI.Analytics | ✅ | ML (10), data science (8), RL (7) components |
| Blazor.AI.LocalModels | ✅ | 11 components; local HF.Net inference verified (sentiment, NER, embeddings, chat, ViT, CLIP, TrOCR) |
| Blazor.AI.Bio | ✅ | 11 components on Biotech.Net 0.2.0, bio agent tools, gallery page with code per use case |
| Blazor.AI.Templates | ✅ | `dotnet new blazorai` (`--provider`, `--agents`); generated app built and run against local packages |
| Gallery (Server) | ✅ | 17 pages, 109 components, preview + code tabs, screenshots in `docs/screenshots` |
| WASM sample | ✅ | Runs fully in the browser with the simulated model; optional local Ollama |
| Tests | ✅ | 25 xUnit/bUnit tests passing (`tests/Blazor.AI.Tests`) |
| NuGet icon | ✅ | `assets/icon.png` (kawung motif) |
| Docs EN / ID | ✅ | README + `docs/en`, `docs/id` (getting started, providers, components, security) |
| Pack | ✅ | All 11 packages pack cleanly (`dotnet pack -c Release`) |
| Monorepo + CI | ✅ | In `Vibe_Components/Blazor.AI`; `.github/workflows/blazor-ai.yml` builds and tests every change and publishes on `blazor-ai-v*` tags |
| Release 1.0.0 | ✅ | Tagged `blazor-ai-v1.0.0` on 2026-10-21; all 11 packages (+ symbols) pushed to NuGet by CI |

## Verified against real services

Azure OpenAI (gpt-5-mini), DeepSeek, Anthropic Claude, TypeSafe System One, local Hugging Face models via HF.Net. Hugging Face hosted inference needs account credits (falls back to local).

## Known limitations

- ImageSharp is pinned to 3.1.12 (last Apache-2.0 release); advisories are suppressed and documented in `docs/en/security.md`.
- SAC continuous-control RL is slow (~4 min for 20 episodes); the UI defaults to 8 episodes and warns.
- Bio `MultipleAlignmentViewer` is interactive up to 40 sequences × 3,000 residues; pairwise alignment up to ~5 kb × 5 kb.

## Notes for the next session

- Inside Razor code blocks, write SVG `<text>` as `<!text>`.
- `[GeneratedRegex]` does not work inside .razor files; put regexes in .cs files.
- Razor attributes can't contain inner `"`; move such lambdas to methods.
- A component named like a library type (e.g. `PrimerDesigner`, `CodonOptimizer`) shadows it; fully qualify the Biotech.Net type.
- Interpolated string concatenations passed to `string.Create(CultureInfo, ...)` must all be `$"..."` literals.
- Stop the running gallery before rebuilding (it locks DLLs).
- Large bash heredocs fail in this environment; write files with the editor tool.
