# Blazor.AI: roadmap

*Dibuat oleh Gravicode Studios dipimpin oleh Kang Fadhil.*

Blazor.AI gives Blazor developers ready-made AI components that work with any model provider, run offline for demos and tests, and keep calculations deterministic in .NET. This plan tracks what ships in each release. Day-to-day status lives in [Progress.md](Progress.md).

*Rencana ini mencatat isi setiap rilis. Status harian ada di [Progress.md](Progress.md).*

## Principles

1. **Provider-agnostic.** Components depend on `Microsoft.Extensions.AI` abstractions only. Adding a provider never changes a component.
2. **Offline first.** Every component works with the simulated model, so the gallery, tests and WebAssembly demos need no keys.
3. **Compute, then explain.** ML, statistics, rules, chemistry and sequence analysis run in .NET (Gravicode.Science, RLNet, TypeSafe, Biotech.Net). The model plans and explains; it never invents numbers.
4. **Safe by default.** Guardrails (PII, moderation, audit, RBAC, encrypted storage) wrap every client.
5. **One design language.** The kawung glass theme, light and dark, mobile-first, keyboard accessible.

## v1.0: foundation (current)

| Area | Package | Scope |
|---|---|---|
| Core | `Blazor.AI.Abstractions`, `Blazor.AI.Core` | 16 provider kinds, simulated model, guardrails, vector index, text services |
| UI | `Blazor.AI.Components` | Smart components, chat/assistant/copilot, generators, coding, productivity, theme |
| Visual | `Blazor.AI.Visualization` | SVG charts, graphs, map, dashboards |
| Knowledge | `Blazor.AI.RAG` | Ingestion, vector search, cited chat, PDF/spreadsheet chat, knowledge graph |
| Agents | `Blazor.AI.Agents` | Tool calling, multi-agent, task board, memory, debugger, MCP, workflows |
| Decision | `Blazor.AI.Decision` | Rules, DMN tables, trees, policy, risk, eligibility, approvals, pricing |
| Analytics | `Blazor.AI.Analytics` | AutoML, workbenches, statistics, Bayesian, time series, signals, RL |
| Local models | `Blazor.AI.LocalModels` | Hugging Face hub, local NLP/vision/OCR/speech |
| Biology | `Blazor.AI.Bio` | Sequences, alignment, phylogeny, proteins, molecules, CRISPR, primers, codons, RNA-seq |
| Tooling | `Blazor.AI.Templates` | `dotnet new blazorai` |
| 3D | `Blazor.AI.ThreeD` (v1.1) | Scene3D engine; avatars, 3D viz, agents, decisions, RL, digital twins, health, engineering, education, gaming, enterprise |
| Samples | Gallery (Server), WASM sample | Every component with code; industry showcases |
| Quality | Tests, docs EN/ID, CI publish | xUnit + bUnit, bilingual docs with screenshots, GitHub Actions |

## v1.1: depth

- Persistent vector stores through `Microsoft.Extensions.VectorData` connectors (Qdrant, PostgreSQL pgvector, Azure AI Search) with a sample.
- Conversation persistence for `AiChat` and agents (pluggable store).
- `AuthenticationStateProvider`-based `IAiUserContext` out of the box.
- Localisation of component UI strings (English, Bahasa Indonesia) through `IStringLocalizer`.
- More bUnit coverage: every component renders with the simulated model.

## v1.2: reach

- 3D: WebXR (VR/AR) mode for Scene3D, instanced rendering for large twins, glTF export of generated CAD/worlds, more rigged characters (Rodin + Blender pipeline in `tools/blender`).

- WebAssembly-friendly server proxy package so WASM apps can use cloud models without exposing keys.
- Voice mode for `AiChat` (speech in, speech out) using the LocalModels speech stack.
- Bio: protein structure viewer (PDB), genome browser track, single-cell UMAP explorer.
- Analytics: model export to ONNX and a model registry panel.

## v2.0: platform

- Visual agent and workflow designer that exports to code.
- Evaluation harness: golden datasets, LLM-as-judge, regression dashboards per component.
- Theming API with design tokens beyond kawung (brand palettes).

## Release process

1. Update `VersionPrefix` in `Directory.Build.props` and the template's package versions.
2. Run `dotnet test` and the gallery smoke screenshots.
3. Tag `blazor-ai-vX.Y.Z` in the monorepo; GitHub Actions builds, tests, packs and publishes to NuGet with the `NUGET_API_KEY` secret.
