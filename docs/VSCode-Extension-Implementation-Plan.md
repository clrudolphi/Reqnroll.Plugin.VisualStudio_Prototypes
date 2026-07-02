# VS Code Extension — Implementation Plan

> **Status (updated 2026-07-02):** Core implementation complete — all tasks in the table below,
> including T11 end-to-end validation, are done. Four items remain open, tracked as GitHub issues
> rather than in this table: T3 (table-cell decorations), T7 (VS-Code-specific LSP spec coverage),
> T8 (fold as-built details into the canonical Architecture/Feature-Designs docs), and T12 (Define
> Step code action has no effect — active bug). Parity fixes made after this doc's original date
> (`reqnroll/projectFiles` wiring, telemetry, shared method-name constants, a `tfmToShort` bug) are
> recorded in project memory, not reflected in the table below — the table is otherwise accurate.
> **Branch:** `feat/vscode-extension-initial`
> **Original date:** 2026-06-30
> **Source:** [Porting-to-VSCode-Rider-Analysis](Porting-to-VSCode-Rider-Analysis.md), [LSP-IDE-Support-Architecture](LSP-IDE-Support-Architecture.md)

---

## Completed

| Task | Deliverable | Status |
|------|-------------|--------|
| **T0** | Extension project scaffolding — `package.json`, `tsconfig.json`, `.vscodeignore`, ESLint, Prettier, TextMate grammar, `extension.ts` stub, Mocha test skeleton, `.vscode/launch.json`, `.vscode/tasks.json` | ✅ |
| **T1** | Multi-platform server publish — `scripts/publish-server.sh`, `scripts/build-vsix.sh`, multi-RID server path resolution in `extension.ts`, CI workflow, Connector `RuntimeIdentifiers` + `CopyConnectorsToPublish` target | ✅ |
| **T2** | Test project scaffolding (`tests/VSCode/`) — standalone npm/Mocha project with compile + discover + execute verified | ✅ |
| **T4** | Semantic token scopes — all 11 `reqnroll.*` server legend types mapped to VS Code TextMate scopes, validation script in CI | ✅ |
| **T5** | TextMate grammar — rewritten with 10 repository entries, separate feature/step keywords, numeric literals, table header separators, 21 grammar tests | ✅ |
| **T6** | Custom notification support — v1 `projectManager.ts` + v2 `msbuildEvaluator.ts` (`dotnet msbuild -getProperty`), connector publish fix | ✅ |
| **T9** | LSP inspector logging — `lspInspectorLogger.ts` with `TeeLogOutputChannel` writes to both VS Code Output panel and `%LOCALAPPDATA%\Reqnroll\reqnroll-vscode-inspector-*.log` in lsp-viewer JSON format, controlled by `reqnroll.trace.server` setting | ✅ |
| **T10** | Status bar — `StatusBarManager` shows `$(loading~spin)` / `$(check)` / `$(error)` reflecting LSP server lifecycle, click reveals output channel | ✅ |
| **T13** | F13 Comment Toggle — `commentToggle.ts` sends `workspace/executeCommand` with `reqnroll.toggleComment`, keyboard shortcut Ctrl+/ (Cmd+/ on Mac) for gherkin files | ✅ |
| **T14** | F14 Find Step Usages — `stepUsages.ts` with `doFindStepUsages`, supports CodeLens click and command palette invocation | ✅ |
| **T15** | F15 Find Unused Step Definitions — `stepUsages.ts` with `doFindUnusedStepDefinitions` | ✅ |
| **T17** | F17 Go to Hooks — `hookNavigation.ts` with quick pick for multiple hooks, full navigation with reveal | ✅ |
| **T18** | F18 Code Lens — `stepCodeLens.ts` registers `CodeLensProvider` for `csharp` language, delegates to `textDocument/codeLens` | ✅ |
| **T11** | End-to-end validation — smoke test confirms extension activates, server starts with `--ide vscode`, semantic tokens, code folding, diagnostics, and code actions all work | ✅ |
| **T4b** | Define Steps / Go to Step Definition / Rename Step — `reqnroll.goToStepDefinition` sends `reqnroll/goToStepDefinitions` with rich quick-pick picker; `reqnroll.defineSteps` delegates to `editor.action.quickFix`; `reqnroll.renameStep` delegates to `editor.action.rename`; F12 and F2 keybindings added for gherkin files | ✅ |

---

## Server-Side Bug Fixes (found during VS Code testing)

These bugs were discovered by exercising the extension and fixed on `feat/vscode-extension-initial`.

| Bug | Root cause | Fix | Tests added |
|-----|-----------|-----|-------------|
| **F2 Rename: "Internal Error" when step has no binding** | `StepRenameHandler.HandlePrepareRenameAsync` returned a range for any step in a `.feature` file without checking if the step was actually defined in the match cache; the rename handler's `null` return was converted to `throw new InvalidOperationException` | (1) `HandlePrepareRenameAsync` now calls `FindBindingsAtFeatureStep` for `.feature` files and returns `null` (→ "Rename not available here") when no defined binding exists. (2) The `rename` null-fallback now returns an empty `WorkspaceEdit` instead of throwing, so VS Code shows nothing rather than "Internal Error". | 2 new scenarios in `RenameSteps.feature` — feature-side rename roundtrip; `prepareRename` suppressed for undefined step |
| **F15 Find Unused: deleted `.cs` file still appears; click throws** | `workspace/didChangeWatchedFiles` with `FileChangeType.Deleted` for `.cs` files was silently ignored by `WatchedFilesHandler`. The binding registry retained the deleted file's step definitions indefinitely. | Added `ICSharpBindingDiscoveryService.RemoveFileAsync`; implemented in `CSharpBindingDiscoveryService` (passes empty text to `ReplaceBindings`, which evicts the old entries and adds zero new ones); `WatchedFilesHandler` now calls `RemoveFileAsync` on `.cs` deletion events. VS Code's LSP client already sends these events via `synchronize.fileEvents: '**/*.{feature,cs}'`. | 1 new spec scenario in `FindUnusedStepDefinitions.feature`; 2 new unit tests in `WatchedFilesHandlerTests` |

---

## Remaining / Known Issues

### Phase 2 — Visual Features (deferred)

#### T3 — TableHighlightService

**Scope:** Client-side per-cell text decorations for Gherkin data tables. LSP semantic tokens cannot express per-pipe granularity — requires a `TextEditorDecorationType` service.  
**Effort:** ~200 lines TypeScript.  
**Source:** PoC `tableHighlightService.ts` (~150 LOC).

### Phase 3 — Spec Tests (deferred)

#### T7 — LSP Protocol-Level Spec Tests for VS Code Client Scenarios

**Scope:** Extend `tests/LSP/Reqnroll.IdeSupport.LSP.Server.Specs/` with `.feature` scenarios simulating VS Code's capability set using `--ide vscode`.  
**Effort:** 2–3 scenarios + fixture updates.  
**Depends on:** Familiarity with existing Specs project structure.

### Phase 4 — Documentation (deferred)

#### T8 — Architecture and Feature-Design Documentation

**Scope:** Update `docs/LSP-IDE-Support-Architecture.md` §6.1 and `docs/LSP-IDE-Support-Feature-Designs.md` with as-built VS Code extension details.  
**Effort:** 1–2 pages of markdown.

### Server-Side Investigation

#### T12 — Define Step Code Action Has No Effect

**Observed:** Code actions for undefined/ambiguous steps appear in the editor, but invoking "Define Step" produces no visible change. The `FeatureCodeActionHandler` returns actions but the `workspace/executeCommand` → scaffolding → `workspace/applyEdit` pipeline doesn't produce output.

**Scope:** Spec tests, server log analysis, `applyEdit` payload format verification.  
**Effort:** 1–2 days.

---

## Extension Architecture

```
extension.ts
  ├── resolveServerPath()          — dev vs. production binary resolution
  ├── LanguageClient               — LSP client (vscode-languageclient v10)
  │   ├── outputChannel            — 'Reqnroll LSP' VS Code output panel
  │   └── traceOutputChannel       — TeeLogOutputChannel (panel + file)
  ├── StatusBarManager             — $(loading~spin) / $(check) / $(error)
  ├── ProjectManager               — reqnroll/projectLoaded notification
  │   └── msbuildEvaluator         — dotnet msbuild -getProperty
  ├── stepCodeLens                 — CodeLensProvider for csharp
  └── Commands (8)
      ├── defineSteps              → editor.action.quickFix  (native code action picker)
      ├── goToStepDefinition       → stepNavigation.ts   (F12; rich quick pick)
      ├── toggleComment            → commentToggle.ts    (Ctrl+/)
      ├── findStepUsages           → stepUsages.ts       (CodeLens + palette)
      ├── findUnusedStepDefinitions→ stepUsages.ts
      ├── goToHooks                → hookNavigation.ts   (quick pick)
      ├── renameStep               → editor.action.rename (F2; native rename)
      └── showOutputChannel        → reveals output panel
```

### Source maps

| Directory | Contents |
|-----------|----------|
| `src/VSCode/` | Extension manifest, configs, scripts |
| `src/VSCode/src/` | 9 TypeScript modules (runtime) |
| `src/VSCode/src/test/` | 3 Mocha test files |
| `src/VSCode/syntaxes/` | TextMate grammar |
| `src/VSCode/scripts/` | Build and validation scripts |
| `tests/VSCode/` | Standalone grammar test project |

---

## Dependency Graph

```
T0 ──→ T4 ──→ T5 ──→ T6
  │                 └──→ T9 (inspector logging)
  ├─→ T1 ──────────→ T11 (end-to-end)
  └─→ T2 (standalone tests)

Post-T6 (feature wiring, all parallel):
  T13 Comment Toggle
  T14 Find Step Usages
  T15 Find Unused Step Definitions
  T17 Go to Hooks
  T18 Code Lens
  T10 Status Bar

Not yet started:
  T3  TableHighlightService
  T7  LSP spec tests
  T8  Documentation
  T12 Define Step bug (server-side)
```

---

## Future Work

### Rider (Phase 2)

After VS Code stabilizes, the analysis recommends tackling Rider with these tasks (renumbered to avoid collision with VS Code tasks):

| ID | Task | Effort |
|----|------|--------|
| R1 | Rider plugin scaffolding (Gradle, plugin.xml, FileType) | ~210 lines |
| R2 | Core LSP server bridge (LspServerSupportProvider, Descriptor) | ~55 lines |
| R3 | ImplicitReferenceProvider for cross-language navigation | ~150 lines |
| R4 | Semantic token TextAttributesKey mapping | ~50 lines |
| R5 | Custom notification transport | ~70 lines |
| R6 | Table cell decoration | ~200–400 lines |
| R7 | Gutter run icons | ~200–400 lines |
| R8 | Failing-step gutter marks | ~200–300 lines |

See the [Porting-analysis](Porting-to-VSCode-Rider-Analysis.md) §7.2 for the full Rider plan.

---

## Risk Register

| ID | Risk | Mitigation |
|----|------|------------|
| R4 | VS Code has no MSBuild project system — `projectLoaded` falls back to folder-prefix membership | v1: folder-prefix. v2: `dotnet msbuild` eval implemented. Linked files not supported. |
| R5 | Maintaining three IDE client codebases simultaneously | LSP server is shared; glue layers are intentionally thin (~500 LOC for VS Code) |
| R8 | CI complexity with .NET + npm + vsce in one pipeline | Decoupled server publish and extension package CI jobs; `tsc-only` fast path |
