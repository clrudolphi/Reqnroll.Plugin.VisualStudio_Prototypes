# Pull Diagnostics — Implementation Plan

> ## ⛔ ABANDONED — do not implement against OmniSharp 0.19.9
>
> **Outcome (2026-06-25):** Implementation was attempted on `feature/pull-diagnostics` and then
> fully reverted. **Pull diagnostics cannot be served on OmniSharp.Extensions.LanguageServer
> 0.19.9.** The library models the *types* but the **server-side (write) JSON converters are
> `NotImplementedException` stubs.** Verified empirically against the shipped DLL:
>
> - `RelatedDocumentDiagnosticReport.Converter.WriteJson` → throws `NotImplementedException`
> - `WorkspaceDocumentDiagnosticReport.Converter.WriteJson` → throws `NotImplementedException`
> - `DocumentDiagnosticReport.Converter.WriteJson` → throws `NotImplementedException`
>
> This affects **both** `textDocument/diagnostic` and `workspace/diagnostic`. The converters are
> pinned by a class-level `[JsonConverter]` attribute (so they cannot be overridden via serializer
> settings), and `WorkspaceDiagnosticReport`/`WorkspaceDiagnosticReportPartialResult` additionally
> have only `internal`/`protected` constructors — there is no supported way to build *or* serialize
> the response. A plain `Diagnostic` serializes fine, confirming the stub is specific to the report
> wrappers. OmniSharp 0.19.9 is the final, abandoned release — there is no fix upstream.
>
> **The only viable path to pull diagnostics** would be to bypass OmniSharp's model types entirely:
> hand-roll the responses as custom POCO/`JToken` DTOs registered via manual `OnRequest` routing
> (the same "custom-DTO tax" noted for 3.18 features). Given that client benefit was already
> unverified, the team chose to **stay on the existing push pipeline** (`DiagnosticsPublishHandler`
> → `textDocument/publishDiagnostics`) instead. See [[omnisharp-lsp-version-ceiling]].
>
> The plan below is retained **as a record only** — the `Library support` line beneath it is now
> known to be wrong (the types are present but not serializable). Do not action it without first
> committing to the custom-DTO approach.

---

> **Status:** ABANDONED (was: Draft for review) — see banner above
> **Audience:** Core team contributors
> **Based on:** LSP 3.17 `textDocument/diagnostic` + `workspace/diagnostic`; migrates the existing push pipeline
> **Library support:** ⚠️ INCORRECT — types are modelled in OmniSharp 0.19.9 but the server-side
> serializers throw `NotImplementedException`. The original (wrong) assumption is preserved below
> for context:
> ~~Already modelled in OmniSharp.Extensions.LanguageServer 0.19.9 (`IDocumentDiagnosticHandler`,
> `IWorkspaceDiagnosticHandler`, `RelatedFullDocumentDiagnosticReport`,
> `RelatedUnchangedDocumentDiagnosticReport`, workspace diagnostic refresh) — no custom DTO plumbing.~~

---

## 1. Nature of the changes

Today diagnostics are **pushed**: [`DiagnosticsPublishHandler`](../../src/LSP/Reqnroll.IdeSupport.LSP.Server/Pipeline/DiagnosticsPublishHandler.cs) listens for `MatchCacheChangedNotification` and fires `textDocument/publishDiagnostics` for the affected URI. The client is a passive recipient and we re-send the full set on every cache change.

LSP 3.17 adds **pull diagnostics**: the server advertises a `diagnosticProvider`, the client requests `textDocument/diagnostic` for the document(s) it cares about (and optionally `workspace/diagnostic` for the whole workspace), and the server answers with a report that supports **result-id caching** — an `Unchanged` report when nothing has moved since the client's last result id, plus a server-driven `workspace/diagnostic/refresh` nudge when state changes.

What this buys us:

- **Client-driven scoping & caching.** The client pulls for the document it is showing and presents a `previousResultId`; unchanged documents return a tiny `Unchanged` report instead of a full re-publish.
- **A workspace report.** `workspace/diagnostic` can surface "undefined step" / "ambiguous step" problems for feature files the user has not opened — today those only appear once a file is opened and parsed.
- **Cleaner lifecycle.** Diagnostics become a function the client calls, decoupled from our internal match-cache event timing.

**Honest scope limit:** pull diagnostics do **not** by themselves resolve the URI-only/`(uri,project)` ambiguity (Q22). A `textDocument/diagnostic` request still carries no project context, so the primary-owner selection rule stays. The gain is caching, on-demand pull, and the workspace report — not multi-project disambiguation. (The workspace variant *could* later emit one report item per `(uri, project)` slot; that is a follow-on, not part of this plan.)

This is a **migration**, so the central concern is coexistence: a URI must not be both pushed and pulled, or the client double-reports. The server negotiates which model to use per the client's advertised capability.

---

## 2. Structural changes to DTOs

Protocol types ship with the library. The new outputs:

| Method | Return type | Notes |
|---|---|---|
| `textDocument/diagnostic` | `RelatedFullDocumentDiagnosticReport` **or** `RelatedUnchangedDocumentDiagnosticReport` | Full report carries `Items : Container<Diagnostic>` + a new `ResultId`; Unchanged carries only the matching `ResultId`. |
| `workspace/diagnostic` | `WorkspaceDiagnosticReport` | `Items` = one `WorkspaceDocumentDiagnosticReport` per feature URI (full or unchanged), each with its `Uri`, optional `Version`, and `ResultId`. |

**Server capability** — advertised in registration options:

```csharp
new DiagnosticsRegistrationOptions {
    DocumentSelector      = FeatureSelector,   // "**/*.feature"
    Identifier            = "reqnroll",
    InterFileDependencies = true,              // a .cs binding edit changes feature diagnostics
    WorkspaceDiagnostics  = true
}
```

**New internal type — the result-id token.** The `Diagnostic` payload itself is unchanged (still produced by `IDiagnosticsAggregator`); we add a stable identity to decide full-vs-unchanged:

```csharp
public readonly record struct DiagnosticResultId(int DocumentVersion, int RegistryVersion)
{
    public string Encode();                       // "v{doc}.r{registry}"
    public static bool TryParse(string? s, out DiagnosticResultId id);
}
```

Both versions are already tracked — `FeatureBindingMatchSet.DocumentVersion` / `.RegistryVersion`. No match-model change.

---

## 3. New classes and methods

| Component | Project / file | Type | Responsibility |
|---|---|---|---|
| `FeatureDiagnosticsComputer` | `LSP.Server/Diagnostics/` (new) | shared service | The single place that turns `(uri, buffer.Tags, matchSet)` into `Diagnostic[]` **and** the current `DiagnosticResultId`. Extracted verbatim from the body of `DiagnosticsPublishHandler.Handle` (primary-owner lookup, Q24 registry-not-ready suppression, `ToLspDiagnostic`). Both push and pull call it — guarantees identical output. |
| `FeatureDocumentDiagnosticHandler` | `LSP.Server/Features/Diagnostics/` | `IDocumentDiagnosticHandler` | Handles `textDocument/diagnostic`. Computes the current result id; if it equals `request.PreviousResultId`, return `RelatedUnchangedDocumentDiagnosticReport`; else a full report. Registered via `AddHandler<T>()` (no `.cs` conflict — `.feature` selector). |
| `FeatureWorkspaceDiagnosticHandler` | `LSP.Server/Features/Diagnostics/` | `IWorkspaceDiagnosticHandler` | Handles `workspace/diagnostic`. Enumerates indexed feature files (`ILspWorkspaceScopeManager.GetIndexedFeatureFiles`), emitting full/unchanged per URI against the client's `previousResultIds`. |
| `DiagnosticsRefreshHandler` | `LSP.Server/Pipeline/` | `INotificationHandler<MatchCacheChangedNotification>` | **Replaces** the push behaviour when pull is negotiated: instead of `publishDiagnostics`, sends `workspace/diagnostic/refresh`. Mirrors the outbound-request pattern of `SemanticTokensRefreshHandler`. |
| `DiagnosticsPublishHandler` | `LSP.Server/Pipeline/` (existing) | modified | Becomes pull-aware: when the client advertised pull support, it no-ops (the refresh handler takes over); otherwise it keeps pushing exactly as today. Reuses `FeatureDiagnosticsComputer` so push and pull never diverge. |

**Negotiation** — read once in `OnStarted`: `clientCapabilities.TextDocument.Diagnostic != null`. Store on the client-context accessor (`SupportsPullDiagnostics`). One server-side switch chooses push **or** pull for a URI — never both.

**Handler skeleton:**

```csharp
public Task<DocumentDiagnosticReport> Handle(DocumentDiagnosticParams request, CancellationToken ct)
{
    var uri = request.TextDocument.Uri;
    var (diagnostics, resultId) = _computer.Compute(uri);     // shared with push

    if (DiagnosticResultId.TryParse(request.PreviousResultId, out var prev) && prev == resultId)
        return Task.FromResult(new RelatedUnchangedDocumentDiagnosticReport { ResultId = resultId.Encode() });

    return Task.FromResult(new RelatedFullDocumentDiagnosticReport {
        ResultId = resultId.Encode(),
        Items    = new Container<Diagnostic>(diagnostics)
    });
}
```

---

## 4. Surfacing in the Visual Studio client

Diagnostics (squiggles + Error List) are rendered natively by the client from whichever transport it negotiated. **No interceptor and no new VSSDK code are required** — but the push/pull switch must match what VS actually supports, or diagnostics either double-up or vanish.

1. **Verify the VS LSP client advertises `textDocument.diagnostic`.** Read the `initialize` capabilities from the `LspInspectorLogger` session log. The VS.Extensibility LSP client supports pull diagnostics in recent VS 2022 builds, but this **must be confirmed against the shipping version we target** before flipping the default. The negotiation gate makes this safe either way:
   - **Advertised →** server answers `textDocument/diagnostic`; `DiagnosticsRefreshHandler` sends `workspace/diagnostic/refresh` on cache changes; the legacy push is suppressed.
   - **Not advertised →** zero behaviour change; `DiagnosticsPublishHandler` keeps pushing `publishDiagnostics` exactly as today.
2. **Refresh transport already proven.** `workspace/diagnostic/refresh` is a server→client request over the same intercepting pipe that already carries `workspace/codeLens/refresh` and semantic-token refresh, so no new pipe wiring in [`ReqnrollLanguageClient`](../../src/VisualStudio/Reqnroll.IdeSupport.VisualStudio.Extension/ReqnrollLanguageClient.cs).
3. **Workspace diagnostics & the Error List.** If VS issues `workspace/diagnostic`, undefined/ambiguous steps in unopened feature files appear in the Error List — a visible behaviour change worth calling out in release notes. If VS only does document pull, the workspace handler stays dorment with no ill effect.
4. **No double-reporting invariant.** The single negotiated switch (push **xor** pull per session) is the safeguard. A focused manual check in the experimental instance — open a feature with an undefined step, confirm exactly one squiggle and one Error List row — is the acceptance gate before enabling pull by default.
5. **Per [[feedback-vs-specific-gating]]**, if VS's pull implementation proves partial (e.g. document pull works but refresh is ignored), keep VS on push via `ClientIdeContext.IsVisualStudio` while VS Code / Rider use pull.

---

## 5. Impact on testing

### 5.1 Approach

- **Refactor-safety first** — extract `FeatureDiagnosticsComputer` and re-point the existing `DiagnosticsPublishHandlerTests` at it **before** adding pull, proving the push output is byte-for-byte unchanged.
- **Server unit** — `FeatureDocumentDiagnosticHandlerTests` and `FeatureWorkspaceDiagnosticHandlerTests`: result-id full/unchanged logic, registry-not-ready suppression, primary-owner selection. Inputs built directly per [[core-tests-avoid-stubidescope]].
- **Negotiation/coexistence** — `DiagnosticsRefreshHandlerTests` + a test asserting push is suppressed when pull is negotiated and vice-versa (the xor invariant).
- **Acceptance (specs)** — `LspServerHarness` spec that negotiates pull, opens a feature with an undefined step, pulls `textDocument/diagnostic`, asserts the full report, pulls again with the returned `previousResultId`, asserts `Unchanged`; then mutates a binding and asserts a refresh + a new full report.

### 5.2 Test conditions

| # | Condition | Expected |
|---|---|---|
| 1 | Pull negotiated, first request (`previousResultId` null), undefined step present | `Full` report with the diagnostic + a `ResultId` |
| 2 | Pull, repeat request with matching `previousResultId`, no change | `Unchanged` report, no `Items` |
| 3 | Pull, repeat after a `didChange` (doc version bumped) | `Full` report, new `ResultId` |
| 4 | Pull, repeat after a registry replacement (binding added) | `Full` report (inter-file dependency), new `ResultId` |
| 5 | Registry `Invalid` (Q24) | Binding-mismatch diagnostics suppressed; parse errors still reported — parity with push |
| 6 | `workspace/diagnostic` over several indexed features | One report item per URI; opened-and-unchanged ones return `Unchanged` |
| 7 | Shared/linked feature, multiple owners | Primary owner's match set used (Q22); single coherent report |
| 8 | Push fallback (pull **not** negotiated) | `publishDiagnostics` fires on cache change; output identical to pre-change baseline |
| 9 | xor invariant | Pull negotiated → no `publishDiagnostics` ever sent for that URI; push mode → no diagnostic-refresh ever sent |
| 10 | `MatchCacheChangedNotification` under pull | Exactly one `workspace/diagnostic/refresh` |

**Regression guard:** keep a golden-output assertion that `FeatureDiagnosticsComputer` produces the same `Diagnostic[]` the old inline push code did, so neither transport can drift from the other.

---

## 6. Phased build plan & effort

### Phase 0 — Capability verification (go/no-go) · ~0.5 day

- In the experimental VS instance, open a feature with an undefined step; from the inspector log check `capabilities.textDocument.diagnostic != null` and whether VS issues `textDocument/diagnostic` (and `workspace/diagnostic`).
- **Decision rule:**
  - Advertised → proceed; pull becomes the negotiated mode for VS.
  - Not advertised → still land the refactor (extract `FeatureDiagnosticsComputer`) and the handlers for VS Code/Rider, but VS keeps the push path. Record in §7.

### Phase A — Extract `FeatureDiagnosticsComputer` (refactor-safety) (~1 day)
Pull the computation out of `DiagnosticsPublishHandler`; re-point existing push tests at it; prove byte-identical output **before** any new behaviour.

### Phase B — Document pull handler + result-id (~1 day)
`FeatureDocumentDiagnosticHandler`, `DiagnosticResultId`, full/unchanged logic, registration + negotiation.

### Phase C — Refresh handler + push/pull switch (~1 day)
`DiagnosticsRefreshHandler`; make `DiagnosticsPublishHandler` pull-aware (no-op under pull); enforce the xor invariant.

### Phase D — Workspace pull + cutover (~1.5 days)
`FeatureWorkspaceDiagnosticHandler`; staged rollout (§8); spec + unit conditions (§5); VS double-report check.

**Total: ~5 days** (the highest of the three — it changes shipping behaviour, so it carries the refactor-safety and cutover overhead).

---

## 7. Cross-client support matrix

| Client | Negotiated mode | Notes |
|---|---|---|
| VS Code | Pull (document + workspace) | Gains Error-List entries for unopened feature files via `workspace/diagnostic` |
| Rider | Pull | JetBrains LSP pull-diagnostic support |
| Visual Studio | TBD by Phase 0 — pull if advertised, else push | Push path is the unchanged status quo |

---

## 8. Cutover & rollback

This is the only one of the three plans that alters a **shipping** behaviour, so it ships dark and ramps:

1. **Land behind a default-off switch** (`reqnroll.diagnostics.mode = push | pull | auto`, default `push`). `auto` = "pull if the client advertised it, else push".
2. **Dogfood** with the switch set to `auto` internally; watch the §9 telemetry for double-report anomalies and refresh-storm counts.
3. **Flip the default to `auto`** once the experimental-instance double-report check (test condition 9 / VS pass) is green across VS, VS Code, Rider.
4. **Rollback** is a one-liner: set the default back to `push`. Because `FeatureDiagnosticsComputer` is shared, push mode is guaranteed to still produce the exact pre-change output.

---

## 9. Push ⊕ pull state machine

The core correctness invariant — a URI is **never** both pushed and pulled in one session:

| Negotiated mode | `MatchCacheChangedNotification` → | `textDocument/diagnostic` → | `publishDiagnostics` sent? | `diagnostic/refresh` sent? |
|---|---|---|---|---|
| **push** (legacy / `auto`+no client support) | `DiagnosticsPublishHandler` pushes | (not received) | yes | never |
| **pull** (`auto`+client support / `pull`) | `DiagnosticsRefreshHandler` nudges | handler answers full/unchanged | never | yes |

The single negotiated `SupportsPullDiagnostics` flag selects the row; nothing toggles mid-session. Tests assert both "never" cells explicitly (test condition 9).

---

## 10. Telemetry

| Property | Type | Purpose |
|---|---|---|
| `DiagnosticsMode` | enum (`push`/`pull`) | The negotiated transport — measures real client support in the field. |
| `DiagnosticReportKind` | enum (`full`/`unchanged`) | Cache-hit rate; validates that result-id caching is actually saving work. |
| `WorkspaceDiagnosticDocCount` | int | Blast radius of `workspace/diagnostic` on real solutions — feeds §11 throttling decisions. |
| `RefreshCount` | counter | Detect refresh storms during discovery. |

---

## 11. `workspace/diagnostic` scale

The workspace report can enumerate every indexed feature file, so it needs guard rails the document pull does not:

- **Partial results / streaming** — the spec supports partial-result reporting for `workspace/diagnostic`; emit in batches rather than one giant payload on large solutions.
- **Result-id reuse** — most documents return `Unchanged`; only changed `(uri)` slots pay the full cost. Verify the unchanged path is genuinely cheap (no re-aggregation).
- **Membership-index interaction** — enumerate via `ILspWorkspaceScopeManager.GetIndexedFeatureFiles`, and for linked/shared features report once under the **primary owner** (Q22) to avoid duplicate workspace entries for the same URI.
- **Cancellation** — honour the token; workspace pulls are long-running and frequently superseded.
- **Result-id cache growth** — `DiagnosticResultId` is a value struct keyed per URI; ensure stale URIs are dropped when a project/file unloads so the cache tracks the membership index.

---

## 12. Risks & open questions

| # | Item | Disposition |
|---|---|---|
| PD-1 | VS advertises pull but ignores `workspace/diagnostic/refresh` | If found in Phase 0/D, VS-gate to push via `ClientIdeContext.IsVisualStudio` ([[feedback-vs-specific-gating]]). |
| PD-2 | Double-reporting during the push→pull transition | Prevented by the xor state machine (§9) + test condition 9; the gate is per-session, not per-message. |
| PD-3 | `workspace/diagnostic` surprises users with Error-List noise for unopened files | Intentional behaviour change — call out in release notes; consider a setting if noisy. |
| PD-4 | Pull does **not** resolve `(uri,project)` ambiguity (Q22) | Out of scope by design (see §1); workspace per-`(uri,project)` reporting is a registered follow-on. |
