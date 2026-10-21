# Blazor.AI: progress

_Last updated: 2026-10-22. Dibuat oleh Gravicode Studios dipimpin oleh Kang Fadhil._

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
| Blazor.AI.ThreeD | ✅ | 48 Three.js components + `Scene3D` engine (vendored Three.js r170, Draco); assets from Rodin + Blender (rigged guide robot with 8 clips and lip-sync morphs, swimming fish, robot arm) and Nano Banana 2 art; all scenes and main interactions verified in the browser with Azure OpenAI |
| Blazor.AI.MediaPipe | ✅ | 72 computer-vision components on MediaPipe.NET (ONNX Runtime, server-side): core viewer/recorder/model manager, face (mesh, blendshapes, attendance, verification), hands and sign language, pose and exercise coaching, holistic and motion capture, segmentation, detection, classification, vision copilot, Three.js views, healthcare, agritech, education, industry, analytics; sample photos from Nano Banana 2; all verified in the browser with sample images and a fake webcam, AI answers verified with Azure OpenAI (image attached) |
| Blazor.AI.Templates | ✅ | `dotnet new blazorai` (`--provider`, `--agents`); generated app built and run against local packages |
| Gallery (Server) | ✅ | 28 pages, 229 components (6 pages under a 3D nav group, 5 under Vision), preview + code tabs, screenshots in `docs/screenshots` |
| WASM sample | ✅ | Runs fully in the browser with the simulated model; optional local Ollama |
| Tests | ✅ | 47 xUnit/bUnit tests passing (`tests/Blazor.AI.Tests`), incl. 3D layouts, palette, avatar brain, companion memory, scene JSON, vision math, masks, face gallery |
| NuGet icon | ✅ | `assets/icon.png` (kawung motif) |
| Docs EN / ID | ✅ | README + `docs/en`, `docs/id` (getting started, providers, components, security, 3D, computer vision); asset pipeline in `tools/blender` |
| Pack | ✅ | All 13 packages pack cleanly (`dotnet pack -c Release`) |
| Monorepo + CI | ✅ | In `Vibe_Components/Blazor.AI`; `.github/workflows/blazor-ai.yml` builds and tests every change and publishes on `blazor-ai-v*` tags |
| Release 1.0.0 | ✅ | Tagged `blazor-ai-v1.0.0` on 2026-10-21; all 11 packages (+ symbols) pushed to NuGet by CI |
| Release 1.1.0 (3D) | ✅ | Tagged `blazor-ai-v1.1.0` on 2026-10-21; all 12 packages (+ symbols) pushed to NuGet by CI |

## Verified against real services

Azure OpenAI (gpt-5-mini), DeepSeek, Anthropic Claude, TypeSafe System One, local Hugging Face models via HF.Net. Hugging Face hosted inference needs account credits (falls back to local).

## Known limitations

- ImageSharp is pinned to 3.1.12 (last Apache-2.0 release); advisories are suppressed and documented in `docs/en/security.md`.
- SAC continuous-control RL is slow (~4 min for 20 episodes); the UI defaults to 8 episodes and warns.
- Vision runs on the server only (ONNX Runtime has no browser WASM build here); about 10–15 fps per viewer on a laptop CPU. The default face embedder is not biometric-grade.
- Bio `MultipleAlignmentViewer` is interactive up to 40 sequences × 3,000 residues; pairwise alignment up to ~5 kb × 5 kb.

## Notes for the next session

- Inside Razor code blocks, write SVG `<text>` as `<!text>`.
- `[GeneratedRegex]` does not work inside .razor files; put regexes in .cs files.
- Razor attributes can't contain inner `"`; move such lambdas to methods.
- A component named like a library type (e.g. `PrimerDesigner`, `CodonOptimizer`) shadows it; fully qualify the Biotech.Net type.
- Interpolated string concatenations passed to `string.Create(CultureInfo, ...)` must all be `$"..."` literals.
- Stop the running gallery before rebuilding (it locks DLLs).
- Large bash heredocs fail in this environment; write files with the editor tool.
- 3D: scenes are declarative (`Object3D` diffed by Id); use `Scene.UpdateAsync` for live changes. Models face +Z. Off-screen scenes render only when content changes. Run one gallery at a time: several galleries plus headless browsers exhausted memory once.
- Blender MCP: in an empty file (`read_homefile`) `bpy.ops` modifier/primitive operators lack context; build meshes with bmesh. NLA strips need the action slot; the `look` frames mode ignores NLA, so check poses with frame_set + angles.
- Vision: `ObjectDetector` boxes are in pixels (normalize with `Overlay.Box`); other results are normalized. Test live paths with Playwright Chromium `--use-fake-device-for-media-stream --use-file-for-fake-video-capture=<file>.y4m`. MediaPipeNet references only OnnxRuntime.Managed, so the package also references `Microsoft.ML.OnnxRuntime` for the native runtime.
