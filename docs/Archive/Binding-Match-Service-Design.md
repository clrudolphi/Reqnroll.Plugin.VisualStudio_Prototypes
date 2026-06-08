# Binding Match Service — Implementation Plan

> **Status:** Draft for implementation
> **Scope:** Section 3 ("Binding Match Service") of [LSP-IDE-Support-Design](./LSP-IDE-Support-Design.md), supporting features **F2** (Binding Discovery), **F3/F4** (Diagnostics), and **F5** (Go to Step Definition) in **Phase 2**.
> **Branch:** `binding_mapping`

---

## Table of Contents

1. [Goal](#1-goal)
2. [Current State](#2-current-state)
3. [Target Architecture](#3-target-architecture)
4. [New Classes and Interfaces](#4-new-classes-and-interfaces)
5. [Refactoring of Existing Classes](#5-refactoring-of-existing-classes)
6. [Pipeline Wiring](#6-pipeline-wiring)
7. [Testing Impacts](#7-testing-impacts)
8. [Implementation Order](#8-implementation-order)
9. [Open Questions](#9-open-questions)

---

## 1. Goal

Introduce a standalone **Binding Match Service** that reconciles a feature file's AST against the per-project **Binding Registry**, caches the results, recomputes them when either the AST or the registry changes, and exposes those results to downstream consumers (Diagnostics Aggregator, Go to Definition, Define Steps).

This decouples *binding matching* from *tag parsing*. Today the two are fused inside `DeveroomTagParser`; the design (§3, item 3) calls for a separate service with its own cache keyed by `(featureURI, range)` and `(csURI, range)` and an independent update lifecycle.

The matching **kernel** — `ProjectBindingRegistry.MatchStep` — is already ported and correct. This work adds an orchestration + caching layer around it and rewires the pipeline; it does **not** reimplement match logic.

---

## 2. Current State

The matching logic was ported from the VS extension but is embedded in the tag parser, with no separate match cache.

| Component | Location | Role today |
|---|---|---|
| `ProjectBindingRegistry.MatchStep` | `LSP.Core/Discovery/ProjectBindingRegistry.cs:52` | Pure, stateless matching kernel → `MatchResult` (Defined / Undefined / Ambiguous + errors). **Keep as-is.** |
| `DeveroomTagParser.Parse` | `LSP.Core/Editor/Services/Parsing/GherkinDocuments/DeveroomTagParser.cs:139` | Parses AST **and** calls `bindingRegistry.MatchStep` inline per step, emitting `DefinedStep`/`UndefinedStep`/`StepParameter`/`BindingError`/`ScenarioHookReference` tags. Parsing + matching are coupled. |
| `GherkinDocumentTaggerService` | `LSP.Server/Services/GherkinDocumentTaggerService.cs` | Orchestrates: buffer → snapshot → `IProjectBindingRegistryLookup.GetRegistryForUri` → `tagParser.Parse` → store tags + invalidate semantic-token cache. |
| `TextDocumentSyncHandler` | `LSP.Server/Handlers/ProtocolHandlers/TextDocumentSyncHandler.cs` | Handles `didOpen`/`didChange`/`didClose`; updates buffer; publishes `GherkinDocumentParsedNotification`. |
| `BindingRegistryChangedHandler` | `LSP.Server/Handlers/InternalHandlers/BindingRegistryChangedHandler.cs` | Reacts to `BindingRegistryChangedNotification` → re-parses (and thus re-matches) every affected open feature file. |

**Does not exist yet:** `LSP.Core/Matching/`, any match cache, a Diagnostics Aggregator, and the `ASTChangedNotification` / `MatchCacheChangedNotification` notifications named in §3/§5.

Consequence today: match results live only inside the re-computed tag tree; there is no way to query "what does this step match?" without re-running the full tag parse, and there is no reverse `(csURI → feature steps)` index for F14/F18.

---

## 3. Target Architecture

```
LSP Client message (didOpen / didChange on .feature)
  → TextDocumentSyncHandler  (Protocol Handler — name unchanged; aligns with the LSP message)
      → [sync] parse AST, store in Document Buffer
      → publishes ASTChangedNotification (async, MediatR)
          → BindingMatchInternalHandler
              → BindingMatchService.Reconcile(uri, ast, registry)   // cache (featureURI, range)
              → publishes MatchCacheChangedNotification
                  → DiagnosticsAggregatorHandler (F3 — downstream)
                      → textDocument/publishDiagnostics

BindingRegistryChangedNotification (from Roslyn/Connector discovery)
  → BindingMatchInternalHandler
      → BindingMatchService.Invalidate(affected docs) + recompute
      → publishes MatchCacheChangedNotification (per affected feature URI)
```

Design invariants:

- **`LSP.Core` stays URI-agnostic.** Core has no dependency on OmniSharp `DocumentUri`. The match cache keys on an opaque `string` document id; the Server layer maps `DocumentUri ↔ string`.
- **Single owner of `MatchStep`.** After this change, only `BindingMatchService` calls `ProjectBindingRegistry.MatchStep`. The tag parser consumes a precomputed match set.
- **Cache validity** is `(DocumentVersion, RegistryVersion)`. `ProjectBindingRegistry.Version` already auto-increments, giving a cheap staleness check.
- **Sync-first, async-rest** (§5) is preserved: the buffer/AST write stays synchronous in the protocol handler; matching and diagnostics fan out asynchronously via MediatR.

---

## 4. New Classes and Interfaces

### 4.1 `LSP.Core/Matching/` (netstandard2.0 — engine + cache)

```csharp
namespace Reqnroll.IdeSupport.LSP.Core.Matching;

// One step's resolved match, with the span it occupies in the feature file.
public sealed record StepBindingMatch(
    GherkinRange Range,                 // (featureURI, range) cache coordinate
    Step Step,
    IGherkinDocumentContext Context,
    MatchResult Result);                // reuse existing MatchResult

// Immutable per-document match set — the value cached against (documentId, version).
public sealed class FeatureBindingMatchSet
{
    public string DocumentId { get; }
    public int? DocumentVersion { get; }
    public int RegistryVersion { get; }              // ProjectBindingRegistry.Version
    public IReadOnlyList<StepBindingMatch> Steps { get; }
    public HookBindingMatchSet Hooks { get; }        // backs ScenarioHookReference
    public StepBindingMatch? FindAt(int offset);     // position lookup for F5
    public IEnumerable<StepBindingMatch> Undefined { get; }  // for F3 / F6
}

public interface IBindingMatchService
{
    // Recompute (or return cached) matches for a document's AST against a registry.
    FeatureBindingMatchSet Reconcile(
        string documentId,
        IGherkinTextSnapshot snapshot,
        DeveroomGherkinDocument ast,
        ProjectBindingRegistry registry);

    bool TryGet(string documentId, out FeatureBindingMatchSet matchSet);

    // Registry replaced → drop cached entries for affected docs (recompute lazily/eagerly).
    void InvalidateAll();
    void Invalidate(string documentId);

    // Reverse index (csURI, range) → matching feature steps.
    // Primary for F14 (Find Usages) / F18 (Code Lens) in Phase 4; build the index now,
    // exploit it later.
    IReadOnlyList<StepBindingMatch> FindUsages(SourceLocation bindingLocation);
}

public sealed class BindingMatchService : IBindingMatchService
{
    // ConcurrentDictionary<string, FeatureBindingMatchSet> forward cache
    // + reverse index keyed on binding SourceLocation, rebuilt on each Reconcile.
}
```

Notes:

- **Reuse** `MatchResult` / `MatchResultItem` / `ParameterMatch` / `GherkinDocumentContextCalculator` unchanged. `Reconcile` walks the AST steps and delegates to `registry.MatchStep(step, context)` exactly as `DeveroomTagParser` does today (`DeveroomTagParser.cs:181`).
- The reverse `(csURI, range)` index is built from each match's `MatchedStepDefinition.Implementation.SourceLocation`.
- `HookBindingMatchSet` wraps `ProjectBindingRegistry.MatchScenarioToHooks` results (`ProjectBindingRegistry.cs:41`) so the hook-reference overlay tag and F17 share one source.

### 4.2 `LSP.Server` — pipeline types

| New type | Folder | Role |
|---|---|---|
| `ASTChangedNotification(uri, version, ast, snapshot)` | `Notifications/` | Renames/supersedes `GherkinDocumentParsedNotification` to match §5 naming. Produced after the buffer/AST is written. |
| `MatchCacheChangedNotification(uri)` | `Notifications/` | Produced by `BindingMatchInternalHandler`; consumed by the Diagnostics Aggregator. |
| `BindingMatchInternalHandler` | `Handlers/InternalHandlers/` | Subscribes to `ASTChangedNotification` **and** `BindingRegistryChangedNotification`; calls `IBindingMatchService.Reconcile` / `Invalidate`; publishes `MatchCacheChangedNotification`. Uses `IProjectBindingRegistryLookup` to resolve the per-URI registry. |

> **Naming decision:** `TextDocumentSyncHandler` is **not** renamed — its name aligns with the `textDocument/didOpen|didChange|didClose` LSP messages it processes. Only the *notification* it publishes is renamed (`GherkinDocumentParsedNotification` → `ASTChangedNotification`); the two may coexist during transition.

### 4.3 Downstream consumer (F3 — designed-for, not the core deliverable)

`DiagnosticsAggregatorHandler` subscribing to `MatchCacheChangedNotification`, merging unmatched-step warnings (from `FeatureBindingMatchSet.Undefined`) with parse errors (from the Document Buffer) into a single `textDocument/publishDiagnostics` per URI (§ F3). Listed here so the match service's output contract is designed to serve it.

---

## 5. Refactoring of Existing Classes

### 5.1 `DeveroomTagParser` — decouple from the registry (central change)

Today: `Parse(snapshot, ProjectBindingRegistry)` calls `MatchStep` inline.

**Recommended (surgical):** change the dependency from the *registry* to the *precomputed match set*:

```
Parse(IGherkinTextSnapshot snapshot, ProjectBindingRegistry registry)
  →  Parse(IGherkinTextSnapshot snapshot, FeatureBindingMatchSet matches)
```

- The parser keeps emitting the binding-overlay tags (`DefinedStep` / `UndefinedStep` / `StepParameter` / `BindingError` / `ScenarioHookReference`) — the span-building helpers (`AddParameterTags`, `GetTextSpan`, etc.) are the right home for them and stay put — but it **looks up** each step's match in `matches` instead of computing it.
- The `bindingRegistry == ProjectBindingRegistry.Invalid` guards (`DeveroomTagParser.cs:178, 216`) become "match set empty/absent" guards.
- The parser becomes registry-agnostic; `BindingMatchService` becomes the single caller of `MatchStep`.

**Sequencing:** the AST must exist before matching. `DeveroomTagParser` already runs the Gherkin parse internally (`DeveroomGherkinParser.ParseAndCollectErrors`, `DeveroomTagParser.cs:63`). Factor that so the AST + parse errors are produced first (available to `BindingMatchService.Reconcile`), then structural + overlay tagging runs against the cached match set.

> **Purist alternative (note, not chosen for Phase 2):** fully split structural tagging from binding-overlay tagging into two passes. More faithful to the §3 module separation, but a larger change to the semantic-token tag contract and not required to land F2/F3/F5.

### 5.2 `GherkinDocumentTaggerService` — invert the order

New flow: parse AST → `BindingMatchService.Reconcile(documentId, snapshot, ast, registry)` → `tagParser.Parse(snapshot, matchSet)` → store tags + invalidate semantic-token cache. It no longer drives matching via the registry directly (the match service does).

### 5.3 `BindingRegistryChangedHandler` — delegate to the match service

Today it re-parses every affected buffer purely to refresh matches. New behaviour: `Invalidate` the match cache for the affected documents and let `BindingMatchInternalHandler` recompute, which then drives diagnostics. The existing "find affected buffers under project folder" logic (`BindingRegistryChangedHandler.cs:49`) is reused. (This handler may be merged into `BindingMatchInternalHandler` or kept as a thin trigger — decided during implementation.)

### 5.4 `TextDocumentSyncHandler` — notification rename only

Publishes `ASTChangedNotification` instead of `GherkinDocumentParsedNotification`. **The class is not renamed.** The synchronous buffer write stays in place.

### 5.5 Unchanged (reused as-is)

`ProjectBindingRegistry`, `MatchResult`, `MatchResultItem`, `ParameterMatch`, `GherkinDocumentContextCalculator`, `IProjectBindingRegistryLookup`, `DocumentBufferService`, `DeveroomTag` / `DeveroomTagTypes`, `GherkinRange`, `IGherkinDocumentContext`.

---

## 6. Pipeline Wiring

| Notification | Produced by | Consumed by |
|---|---|---|
| `ASTChangedNotification` | `TextDocumentSyncHandler` (after buffer/AST write) | `BindingMatchInternalHandler` |
| `BindingRegistryChangedNotification` | Roslyn / Connector discovery | `BindingMatchInternalHandler` |
| `MatchCacheChangedNotification` | `BindingMatchInternalHandler` (after match + overlay-tag rebuild) | `DiagnosticsAggregatorHandler` (F3), **`SemanticTokensRefreshHandler`** |

DI registration: add `IBindingMatchService → BindingMatchService` (singleton, holds the cache) and the new internal handler to the server composition root alongside the existing MediatR handlers.

### 6.1 Semantic-token refresh sequence (must be preserved)

The change must preserve the end-to-end chain: *text change → AST reparse → match-cache invalidate (this URI) → rematch against registry → semantic tags invalidated + refresh notification → tags lazily recomputed on the client's next request.*

```
textDocument/didChange (.feature)
  → TextDocumentSyncHandler
      → [sync] DocumentBuffer.Update(uri, version, text); parse AST; store AST   // sync-first (§5)
      → publishes ASTChangedNotification
          → BindingMatchInternalHandler
              → BindingMatchService.Invalidate(uri)                 // (3) cache invalidated for URI
              → BindingMatchService.Reconcile(uri, ast, registry)   // (4) rematch against registry
              → rebuild overlay tags from the match set;
                DocumentBuffer.UpdateTags(uri, tags);
                SemanticTokenService.InvalidateCache(uri)           // (5a) semantic tags invalidated
              → publishes MatchCacheChangedNotification
                  → SemanticTokensRefreshHandler
                      → (debounced) workspace/semanticTokens/refresh // (5b) refresh notification
                  → DiagnosticsAggregatorHandler (F3)
                      → textDocument/publishDiagnostics

textDocument/semanticTokens/full   (client's next request)
  → SemanticTokensHandler → SemanticTokenService.GetSemanticTokensAsync
      → cache miss → encode from current buffer tags                 // (6) recomputed lazily
```

Two wiring decisions are required for this to be correct:

1. **Tag rebuild + `SemanticTokenService.InvalidateCache(uri)` move to *after* matching.** Today both sit synchronously inside `GherkinDocumentTaggerService.ParseAsync` because matching is inline in the parser (`GherkinDocumentTaggerService.cs:55-61`). Once matching is async, the binding-overlay tags (`DefinedStep`/`StepParameter`/`UndefinedStep`) only exist post-match, so the `UpdateTags` + `InvalidateCache` step moves into the post-match flow. Evicting the token cache *before* the new tags exist would cause the client to re-encode pre-binding tokens — the exact stale-token bug that the existing `InvalidateCache` comment guards against.

2. **`SemanticTokensRefreshHandler` re-subscribes from `GherkinDocumentParsedNotification` to `MatchCacheChangedNotification`.** If the refresh still fired on the AST-parse notification it would race ahead of matching and the client would pull stale (uncoloured-binding) tokens. The debounce window (`SemanticTokensRefreshHandler.cs:23`) is retained.

**Tolerated window:** because the AST write is synchronous but matching is async, a `semanticTokens/full` request that arrives between `didChange` and match completion encodes from not-yet-rematched tags. This is benign and consistent with §5 (semantic tokens are "Medium" priority; <200 ms coloring lag is tolerable); the post-match refresh closes the window. Instant keyword coloring independent of discovery would require a two-phase tag publish (structural tags synchronously, binding overlay after match) — an optional enhancement, not required for the sequence above. See **Q-BM5**.

> **Note on double-parse:** the synchronous step parses the Gherkin to produce the AST, and the post-match overlay-tag rebuild runs `DeveroomTagParser.Parse(snapshot, matchSet)` which parses again. For Phase 2 this duplicate parse is acceptable (the tagger already re-parses on every change today). Caching the parsed AST on the buffer and having the overlay step consume it — avoiding the second parse — is the optimization tracked in **Q-BM1**.

---

## 7. Testing Impacts

### 7.1 Existing tests to change

- **`DeveroomTagParserTests`** (`tests/LSP/Reqnroll.IdeSupport.LSP.Core.Tests/Editor/Services/DeveroomTagParserTests.cs`) — `ParseTags` currently passes a `ProjectBindingRegistry` (line 44). Update the helper to first build a `FeatureBindingMatchSet` (via the match service) and pass that in. Structural-tag assertions (FeatureBlock, comments, tables, parse errors) are unaffected; only the binding-overlay cases (`DefinedStep`/`UndefinedStep`/`StepParameter`) change their wiring.
- **`ProjectBindingRegistry*Tests`** (Match / MultiMatch / Ambiguous / Undefined / Cache) — **unchanged**, since `MatchStep` is untouched. They are the regression net guaranteeing the refactor preserves matching semantics.

### 7.2 New unit tests — `LSP.Core.Tests/Matching/`

- **`BindingMatchServiceTests`** — cache hit/miss by `(documentVersion, registryVersion)`; `Reconcile` returns expected Defined/Undefined/Ambiguous sets; `InvalidateAll` / `Invalidate` force recompute; a registry-version bump invalidates; `FindUsages` (reverse index) returns the right steps. Reuse the `RegistryWith` / `GivenBinding` builders from `ProjectBindingRegistryTestsBase` / `DeveroomTagParserTests`.

### 7.3 New server tests — `LSP.Server.Tests/`

- **`BindingMatchInternalHandlerTests`** — `ASTChangedNotification` → `MatchCacheChangedNotification` published; `BindingRegistryChangedNotification` invalidates + recomputes affected URIs only.

### 7.4 Integration specs — `LSP.Server.Specs` (Phase 2 verification gate, §9)

Add `.feature` specs (under `Features/Discovery/` or a new `Features/Matching/`) asserting that after discovery + open: matched steps produce `DefinedStep` semantic tokens and unmatched steps produce `UndefinedStep` (and, once F3 lands, diagnostics) — exercising the full sync → match → diagnostics pipeline. Existing `SemanticTokens.feature` + `SemanticTokenDecoder` infrastructure is reusable.

---

## 8. Implementation Order

1. **Core** — add `LSP.Core/Matching/` (`StepBindingMatch`, `FeatureBindingMatchSet`, `HookBindingMatchSet`, `IBindingMatchService`, `BindingMatchService`). Unit-test against the existing registry builders. No pipeline changes yet.
2. **Core refactor** — change `DeveroomTagParser` to consume a `FeatureBindingMatchSet`; update `DeveroomTagParserTests`. Verify `ProjectBindingRegistry*Tests` stay green.
3. **Server wiring** — add `ASTChangedNotification`, `MatchCacheChangedNotification`, `BindingMatchInternalHandler`; update `GherkinDocumentTaggerService`, `TextDocumentSyncHandler` (notification only), and `BindingRegistryChangedHandler`; register DI.
4. **Server tests + specs** — `BindingMatchInternalHandlerTests` and the integration spec.
5. **(Follow-on, F3)** — `DiagnosticsAggregatorHandler` consuming `MatchCacheChangedNotification`.

Each step keeps the build and the existing test suite green before the next begins.

---

## 8a. Implementation status (as built)

The first increment (Core engine + server wiring) is implemented on `binding_mapping`. During implementation one coupling surfaced that shifted the data-flow direction from §5.1 as originally written; the rest of the plan stands.

**Discovered coupling.** `ProjectBindingRegistry.MatchStep` requires an `IGherkinDocumentContext`, and today that context *is* the `DeveroomTag` tree (`DeveroomTagParser` passes the `ScenarioDefinitionBlock` tag). Step text *spans* are likewise computed by the parser's span helpers. Match computation, context, and span math are therefore all naturally available only during the parse pass. Rebuilding an independent context/span path in the service would duplicate tested logic with divergence risk.

**Chosen seam (lower risk, same outcome).** Rather than have the parser consume a precomputed match set (the §5.1 "recommended" direction), the match set is **projected from the parser's output**: `DeveroomTagParser` is unchanged and still emits `DefinedStep`/`UndefinedStep` tags that carry the step span (`GherkinRange`) and the computed `MatchResult`. `FeatureBindingMatchSet.FromTags(...)` reads those tags into the cache. The Binding Match Service is thus the queryable, invalidatable **cache + reverse index** over match results — match *computation* stays in the parse pass. This delivers the §3 contract (cache keyed by `(featureURI, range)`, reverse `(csURI, range)` index, invalidation, drives diagnostics + the re-pointed refresh) without the risk of a second, divergent matcher.

**Consequence for the pipeline.** Because matching rides along with parsing, there is **no separate async `BindingMatchInternalHandler`** for the parse path: `GherkinDocumentTaggerService.ParseAsync` parses, stores tags, builds + stores the match set, and evicts the semantic-token cache — synchronously — and the existing handlers (`TextDocumentSyncHandler`, `BindingRegistryChangedHandler`, `ReqnrollConfigChangedHandler`) publish `MatchCacheChangedNotification` afterward. `SemanticTokensRefreshHandler` now consumes `MatchCacheChangedNotification`. The end-to-end sequence in §6.1 is preserved exactly; the registry-change path "rematches" by re-parsing the affected open documents, which re-stores their match sets.

**Built in this increment:**
- `LSP.Core/Matching/`: `StepBindingMatch`, `FeatureBindingMatchSet` (+ `FromTags`), `IBindingMatchService`, `BindingMatchService` (cache + reverse `FindUsages` index). 15 unit tests (`BindingMatchServiceTests`).
- Server: `MatchCacheChangedNotification` (replaces `GherkinDocumentParsedNotification`); `GherkinDocumentTaggerService` builds + stores the match set; `SemanticTokensRefreshHandler` re-pointed; `TextDocumentSyncHandler` invalidates the match cache on close; DI registration of `IBindingMatchService` (singleton). Server tests updated; tagger test asserts the store + cache eviction.
- Verified: Core 120 tests, Server 132 tests, Server integration specs 23/41 (rest platform-skipped) all green; full solution builds.

**Deferred (unchanged from plan):** `DiagnosticsAggregatorHandler` (F3) consuming `MatchCacheChangedNotification`; `FeatureDefinitionHandler` (F5) consuming `FeatureBindingMatchSet.FindAt`; a dedicated matching integration spec; the reverse index is built but not yet consumed (F14/F18).

## 9. Open Questions

- **Q-BM1 — Reconcile granularity.** §3 says "reconcile the *affected region* of the AST." For Phase 2, `Reconcile` recomputes the whole document (the Gherkin parser already re-parses the whole file per §3). Region-level incremental matching is a later optimization; the cache key remains per `(documentId, version)`.
- **Q-BM2 — Registry-change recompute: eager vs. lazy.** On `BindingRegistryChangedNotification`, recompute all open affected feature files eagerly (needed to push diagnostics) but invalidate-only for non-open documents. Confirm against the F3 diagnostic-ownership note (feature URIs only).
- **Q-BM3 — Merge `BindingRegistryChangedHandler` into `BindingMatchInternalHandler`?** Both react to registry changes; consolidating avoids a double re-parse. Decide during step 3.
- **Q-BM4 — Reverse-index lifetime.** The `(csURI, range)` index is built now but only consumed in Phase 4 (F14/F18). Confirm it should be maintained from the start rather than deferred.
- **Q-BM5 — One-phase vs. two-phase tag publish.** Phase 2 builds all `.feature` tags (structural + binding overlay) in a single post-match pass, so binding coloring and keyword coloring arrive together after matching. A two-phase publish (structural tags synchronously on parse, binding overlay after match) would give instant keyword coloring before discovery completes, at the cost of two refreshes per change. Deferred unless the post-match latency proves visible.
