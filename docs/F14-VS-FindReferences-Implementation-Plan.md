# F14 — VS "Find All References" from the C# editor: implementation plan

**Status:** Implemented (branch `vs_find_references`) — Surface 3 (Shift+F12 takeover) deferred
**Feature:** F14 (Find Step Definition Usages) — Visual Studio *client* side
**Server side:** `StepReferencesHandler` and `FindStepUsagesHandler` implemented ([Program.cs](../src/LSP/Reqnroll.IdeSupport.LSP.Server/Program.cs) wires both).
**Branch:** `vs_find_references`

## 1. Goal

When the caret is on a binding attribute (`[Given]`/`[When]`/`[Then]`) in the C# editor, show the
feature-file steps that match that binding, surfaced three ways:

1. **Extensions menu** item.
2. **C# editor context-menu** item.
3. **Takeover of Shift+F12 and the existing "Find All References" context-menu item**, falling back
   to the built-in behaviour when the caret is *not* on a binding attribute.

Results display in the standard **Find All References** tool window. **Decision (settled):** a
binding attribute with no matching steps displays an empty window labelled **"0 usages"** — it does
*not* fall back to C#. Fallback to the built-in command happens **only** when the caret does not
resolve to a binding at all.

## 2. Why the design splits across two extension models

The repo is a hybrid extension: a VS.Extensibility extension (`RequiresInProcessHosting = true`)
hosting `ReqnrollLanguageClient`, plus a classic VSSDK `AsyncPackage` and a MEF `VSSDKIntegration`
assembly. In-process hosting lets one process reach both worlds.

Two hard constraints drive the split:

- **VS will not route `textDocument/references` for a `.cs` file to our server** — the client only
  registers the Gherkin document type, and the C# server owns `.cs` unconditionally (confirmed
  in the earlier VS experiment). We must therefore issue the request ourselves over the pipe we
  already own (`LspInterceptingPipe`).
- **The declarative VS.Extensibility command model has no synchronous pass-through** and cannot
  re-bind a built-in command. So surfaces 1 and 2 (additive menu items) fit the declarative model,
  but surface 3 (conditional takeover + fallback) must use a classic `IOleCommandTarget` editor
  command filter. Shift+F12 and the context "Find All References" item raise the *same* command id,
  so one filter covers both.

## 3. Target architecture

```
            ┌────────────────────────── VS process ──────────────────────────┐
 Surface 1  │  VS.Extensibility Command  ──┐                                  │
 (Ext menu) │  (ExtensionsMenu placement)  │                                  │
 Surface 2  │  VS.Extensibility Command  ──┤                                  │
 (ctx menu) │  (VsctParent IDM_VS_CTXT_   │      FindStepUsagesService        │
            │   CODEWIN)                   ├──►  · caret → (uri,line,char)     │
 Surface 3  │  IOleCommandTarget filter  ──┘      · inject textDocument/       │
 (Shift+F12 │  (MEF, VSSDKIntegration)            references over owned pipe   │
  + builtin │   · consume → query → show          · render via                │
  takeover) │   · else re-dispatch built-in       IFindAllReferencesService   │
            │                                          │                       │
            │      LspInterceptingPipe ◄───────────────┘ (req/resp correlation)│
            └──────────────────────────────│──────────────────────────────────┘
                                            ▼  stdio
                                  LSP server: StepReferencesHandler
```

`FindStepUsagesService` is the single shared core all three surfaces call.

## 4. Net-new production work (independent of experiments)

These are required regardless of experiment outcomes; experiments de-risk *how* they're built.

- **P1 — Server contract: three-state result.** Today `StepReferencesHandler` cannot distinguish
  "not a binding" from "binding with zero usages": `BindingMatchService.FindUsages` matches against
  the match cache, and the cache only contains bindings that already have ≥1 matching step
  ([BindingMatchService.cs:62-87](../src/LSP/Reqnroll.IdeSupport.LSP.Core/Matching/BindingMatchService.cs)).
  Add a binding-registry lookup ("is there a binding at this `SourceLocation` in an owning project?")
  and encode the three states on the existing `textDocument/references` response:
  - **`null`** → no binding at this location → client falls back to built-in.
  - **empty array** → binding present, zero matching steps → client shows **"0 usages"**.
  - **non-empty array** → the matching feature steps.

  (Reusing `textDocument/references` avoids a protocol addition; if the registry lookup proves
  awkward, fall back to a custom `reqnroll/findStepUsages` request returning a richer DTO.)
- **P2 — Owned-pipe request/response.** `LspInterceptingPipe` currently only injects one-way
  notifications. Add request correlation: allocate a **string** id with a distinctive prefix
  (e.g. `reqnroll-far-{guid}`) to guarantee no collision with VS's numeric ids, write the request
  frame, await a `TaskCompletionSource`, and have a receive-side interceptor recognise that id,
  **consume** the response (so it is never forwarded to VS, which never sent the request), and
  complete the task.
- **P3 — `FindStepUsagesService`.** Caret → `(uri, line, char)`; call P2; map the result to the
  three states; render via `IFindAllReferencesService.StartSearch(label)` on the UI thread
  (JoinableTaskFactory switch — the LSP response arrives on a background thread).
- **P4 — Surface 1 & 2 commands** (declarative VS.Extensibility), both delegating to P3.
- **P5 — Surface 3 command filter** (MEF `IVsTextViewCreationListener` in `VSSDKIntegration`):
  intercept the FAR command id, consume, call P3; on `null` result re-dispatch the built-in command
  behind a re-entrancy guard.

## 5. Experiments

Spikes are **throwaway**: prove the mechanism, record the finding here and in memory, then
**re-implement cleanly** on the feature branch. Two exceptions (E2, noted) leave a kept asset.
Spike branches are named `spike/far-*` and are **not merged**.

| # | Status | Question it answers | Keep code? |
|---|---|---|---|
| **E1** | ✅ Done | GUID=`{1496A755-94DE-11D0-8C3F-00C04FC2AAE2}`, ID=97 (`VSStd97CmdID.FindAllReferences`); Shift+F12 and context item are the same command. | n/a (manual) |
| **E2** | ✅ Done | Caret on attribute line resolves. `SourceLocation` span starts from attribute; 2-line leeway covers method-signature line. | **Yes** — kept spec scenario |
| **E3** | ✅ Skipped | Design proven sound by analysis; `LspInterceptingPipe.SendRequestToServerAsync` implemented directly. | No spike needed |
| **E4** | ✅ Skipped | `IFindAllReferencesService` API confirmed from `Shell.Framework.dll` without VS. | No spike needed |
| **E5** | ⏳ Deferred | Surface 3 (`IOleCommandTarget` filter, Shift+F12 takeover) is out of scope. | n/a |
| **E6** | ✅ Done | Caret access works; `VsctParent(guidSHLMainMenu, IDG_VS_CODEWIN_NAVIGATETOLOCATION=0x02B1)` confirmed in VS Exp after instance reset. End-to-end golden path passes. | Yes (production code) |
| **E7** | ⏳ Outstanding | VS2022 cross-version validation. | n/a |

### Experiment detail

- **E1 — command id.** Capture via `EnableVSIPLogging` (registry) or DTE command interception while
  pressing Shift+F12 and clicking the context item. Output: the `Guid` + `uint` id used by both.
  Blocks P5. ~½ day, no code.
- **E2 — position granularity (highest correctness risk).** `SameLocation` matches **file + line
  only**, ignoring column. The matched step's `BindingLocations` line is whatever discovery recorded
  (method vs attribute line). Drive `textDocument/references` at the attribute line, the method
  signature line, and the body, through `LspServerHarness` in `LSP.Server.Specs`; observe which
  resolve. Decide the rule (likely: server tolerates "caret anywhere within the attribute-or-
  signature span of a binding"). Outcome feeds P1. Lands as a kept `.feature` integration test.
- **E3 — pipe request/response.** Validate the string-id-prefix scheme and the consume-on-receive
  interceptor against the running server (in-proc harness or VS). The risk is purely
  channel-sharing with VS's own JSON-RPC; prove it before building P2 for real.
- **E4 — results window.** Hardcode a couple of fake `.feature` references; confirm window title,
  grouping, double-click navigation. Validates the `ITableDataSource`/entry shape before wiring P3.
- **E5 — command filter + fallback.** Intercept E1's id, log + consume, then re-dispatch the
  built-in (`IOleCommandTarget`/`DTE.ExecuteCommand`) under a re-entrancy guard; confirm C# Find
  All References still runs and the filter does not loop.
- **E6 / E7** are confidence checks built directly on the feature branch.

## 6. Phasing and branch strategy

**Single feature branch for production:** `vs_find_references` (current). All `spike/far-*` branches
fork from it, are explored, and discarded; findings are written back into this doc.

- **Phase 0 — Decisions & server contract.** **DONE (2026-06-08)**
  - 0-usages behaviour settled: "0 usages" window (not fallback to C#).
  - E2 confirmed — `StepDefinitionFileParser.GetSourceLocation` starts the span from the first
    step-def/hook attribute; `BindingMatchService.SameLocation` uses range check with 2-line
    backward leeway; `HasBindingAtLocation` added to `IProjectBindingRegistryLookup` /
    `BindingRegistryProviderRouter`. Two kept spec scenarios pass: E2 (attribute-line resolution)
    and P1 (non-binding line → 0 results via `textDocument/references`).
  - **Known limitation:** `textDocument/references` returns empty for both "not a binding" and
    "0 usages" because OmniSharp's `LocationOrLocationLinks` converter cannot serialize null.
    The full three-state contract is delivered by the custom `reqnroll/findStepUsages` request
    (see Phase 2).

- **Phase 1 — De-risk (parallel spikes).** **DONE (2026-06-08/09)**
  - **E1** — FAR command id confirmed: GUID `{1496A755-94DE-11D0-8C3F-00C04FC2AAE2}`, ID 97
    (`VSStd97CmdID.FindAllReferences`). Shift+F12 and the context item are the same id.
  - **E3** — skipped; design proven sound by analysis, implemented directly as P2.
  - **E4** — skipped; `IFindAllReferencesService` API confirmed from `Shell.Framework.dll` binaries.

- **Phase 2 — Shared plumbing.** **DONE (2026-06-08/09)**
  - **P2** — `LspInterceptingPipe.SendRequestToServerAsync` injects a JSON-RPC request with
    string id prefix `reqnroll-far-{guid}`.  `TryCompleteCorrelatedResponse` in the receive pump
    consumes the matching response before VS can see it and completes the awaiting TCS.
    `Dispose` faults all in-flight requests.
  - **P3 (core)** — `FindStepUsagesService` (`FindStepUsages/` folder, VS Extension assembly)
    sends `reqnroll/findStepUsages` over the owned pipe, maps the result to
    `StepUsagesResult` (three-state: `NotABinding` / 0-usages / N-usages).
  - **P2b (custom request) — `reqnroll/findStepUsages`.** `FindStepUsagesHandler`
    ([Handlers/ProtocolHandlers/FindStepUsagesHandler.cs](../src/LSP/Reqnroll.IdeSupport.LSP.Server/Handlers/ProtocolHandlers/FindStepUsagesHandler.cs))
    implements the full three-state contract via `FindStepUsagesResponse`:
    `{isBinding:false}` = not a binding (client falls through); `{isBinding:true,locations:[]}` =
    binding with 0 usages; `{isBinding:true,locations:[...]}` = usages with `stepText`/`keyword`/
    `scenarioName`/`projectName` populated from the in-memory snapshot.
    **As-built note:** returns `{isBinding:false}` rather than JSON null — OmniSharp's `OnRequest`
    framework sends an error response for null returns from custom-method handlers (affects the
    spec harness and production alike), so `IsBinding=false` is the "not a binding" sentinel.
    Unit tests in `FindStepUsagesHandlerTests.cs` cover all three states.
  - **P3b (rendering)** — `FeatureReferenceTableEntry`, `FeatureReferencesDataSource`,
    `FindStepUsagesRenderer` (UI-thread switch, `SVsFindAllReferences`, `StartSearch` →
    `AddSource`). VS-validated: FAR window opens with correct rows and working navigation.
  - **DI fix** — `FindStepUsagesState` singleton registered in `ExtensionEntrypoint.InitializeServices`;
    `ReqnrollLanguageClient` populates it on server-init. `FindStepUsagesCommand` injects only
    `(FindStepUsagesState, TraceSource)` — both are DI-resolvable contributions.
  - All 36 spec scenarios and 214 unit tests pass.

- **Phase 3 — Additive surfaces.** **DONE (2026-06-09)**
  - **P4** — `FindStepUsagesCommand`
    ([FindStepUsages/FindStepUsagesCommand.cs](../src/VisualStudio/Reqnroll.IdeSupport.VisualStudio.Extension/FindStepUsages/FindStepUsagesCommand.cs))
    — `[VisualStudioContribution]` command; `GetActiveTextViewAsync` → line/char → `FindStepUsagesService`
    → `FindStepUsagesRenderer`.
  - **Surface 1** (Extensions menu) — live and VS-validated end-to-end.
  - **Surface 2** (C# editor context menu) — `CommandPlacement.VsctParent(guidSHLMainMenu,
    IDG_VS_CODEWIN_NAVIGATETOLOCATION=0x02B1, priority=0x0100)`. Item appears next to "Find All
    References". **E6 confirmed (2026-06-09):** requires a fresh experimental-instance reset
    after deploying, then item appears and produces results.
    **No VSCT, no VSSDK command table:** custom `.vsct`, `<VSCTCompile>`, pkgdef `[$RootKey$\Menus]`,
    and `[ProvideMenuResource]` were all removed. Built-in group targeting requires only
    `guidSHLMainMenu` (`{D309F791-903F-11D0-9EFC-00A0C911004F}`) — verified against `vsshlids.h`.

- **Phase 4 — Takeover surface.** **DEFERRED.** Surface 3 (Shift+F12 / built-in "Find All References"
  takeover via `IOleCommandTarget` filter) is out of scope for this branch. E5 spike and P5
  implementation are not planned.

- **Phase 5 — Cross-version validation.** **E7** (VS2022 + VS2026) still outstanding.

Rationale for ordering: Phase 1 front-loads the three mechanism unknowns (pipe RPC, results window,
command id) that could each invalidate the approach; the additive surfaces (Phase 3) ship a usable
feature before the riskier takeover work (Phase 4); VS2026 parity is a late confirmation, not a
separate design.

## 7. Open unknowns — resolved or deferred

- **U1 (RESOLVED — acceptable for v1)** — Unsaved `.cs` edits do not resolve. The VS client filter
  is Gherkin-only, so the server never receives `.cs` `didOpen/didChange`. References resolve
  against compiled/discovered bindings (Connector path). Confirmed acceptable for v1.
- **U2 (RESOLVED)** — `HasBindingAtLocation` was added to `IProjectBindingRegistryLookup` and
  implemented in `BindingRegistryProviderRouter` (Phase 0).
- **U3 (OPEN)** — VS2026 (VS 18) API parity for `IFindAllReferencesService` and VS.Extensibility
  placements. Expected fine; E7 confirms.

## 8. Done criteria — status

- ✅ Caret on a binding attribute → matching feature steps in FAR window (Surfaces 1 & 2, VS2026 Exp).
- ✅ Binding attribute with no matches → window showing **"0 usages"** (tested via unit tests; VS E6 not
  explicitly exercised for 0-usages but code path is the same).
- ⏳ Surface 3 (Shift+F12 / context "Find All References" takeover) — deferred.
- ⏳ E7 cross-version validation (VS2022) — outstanding.
