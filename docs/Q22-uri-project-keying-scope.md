# Q22 — `(uri, project)` keying for shared/linked feature files — Scope

> Renamed from "Q18" to avoid a number collision with the unrelated Q18 (local log-file sink)
> already in the open-questions table of [LSP-IDE-Support-Design.md](LSP-IDE-Support-Design.md).

**Status:** Implemented (`refactor_to_support_linked_files`, 2026-06-07)
**Depends on:** Q17 membership index (`refactor_to_support_linked_files`)
**Addresses:** Anomaly B — a feature URI owned by N projects is routed and cached as if it had exactly one owner.

---

## 1. Problem

After Q17 a single feature URI can map to **N owning projects** (a `.feature` linked into
several `.csproj`, e.g. `Calculator.feature` living in `Minimal` and linked into
`ExternalReferences`). Three pieces of state still assume **one owner per URI**:

| State | Key today | Where |
|---|---|---|
| Binding-registry routing | `ResolveOwners(uri).FirstOrDefault()` | [`BindingRegistryProviderRouter.GetRegistryForUri`](../src/LSP/Reqnroll.IdeSupport.LSP.Server/Discovery/BindingRegistryProviderRouter.cs) |
| Binding match cache | `documentId` (URI string) | [`BindingMatchService`](../src/LSP/Reqnroll.IdeSupport.LSP.Core/Matching/BindingMatchService.cs) |
| Tags / semantic-token coloring | `(uri, version)`, tags derived from one registry | [`GherkinDocumentTaggerService.ParseAsync`](../src/LSP/Reqnroll.IdeSupport.LSP.Server/Services/GherkinDocumentTaggerService.cs), [`SemanticTokenService`](../src/LSP/Reqnroll.IdeSupport.LSP.Server/Services/SemanticTokenService.cs) |

### Symptoms
1. **Nondeterministic display.** `FirstOrDefault()` returns owners in baseline-arrival order;
   which project's registry colors/squiggles a shared feature depends on startup timing.
2. **Wrong-owner matching.** A feature opened in its *home* project is matched against a
   *different* owner's registry. Benign only while the registries are identical (the linked-`.cs`
   case); with divergent bindings it produces undefined-step squiggles against the wrong project.
3. **Cross-project Find-Usages bleed.** [`BindingMatchService.FindUsages`](../src/LSP/Reqnroll.IdeSupport.LSP.Core/Matching/BindingMatchService.cs)
   scans **every** cached match set and compares by source-file+line only. Match sets from
   unrelated projects can satisfy the comparison, so F14 (and future F18 code-lens counts) can
   report usages computed against the wrong registry.

---

## 2. Why this can't be solved by keying alone

LSP delivers **one** result per URI for the things the editor renders:

- `textDocument/semanticTokens/full` → one token set per open document.
- `textDocument/publishDiagnostics` → one diagnostic set per URI (a partial set *clears* the rest).

The protocol carries **no project context** on these requests. So even with perfect
`(uri, project)` keying internally, for an **open** feature we must still **pick one owner to
present**. The fix therefore has two distinct halves:

- **2A — Presentation disambiguation** (must pick one owner; deterministic policy).
- **2B — Per-`(uri, project)` match cache** (correctness for cross-project queries: F14/F18, and
  future F5).

These can ship independently. 2A removes every *user-visible* symptom (nondeterminism +
wrong-owner squiggles). 2B is the deeper correctness fix for the query features.

---

## 3. Design

### 3.1 Primary-owner policy (2A)

Add a deterministic resolver to `ILspWorkspaceScopeManager`:

```csharp
LspReqnrollProject? ResolvePrimaryOwner(DocumentUri uri);
```

Policy (first match wins, fully deterministic):
1. **Home project** — the owner whose `ProjectFolder` is a path-prefix of the file; if several,
   the longest prefix (closest containing project).
2. **Stable fallback** — for a file outside every owner's folder (a genuinely external/shared
   location), the owner with the ordinally-smallest `ProjectFullName`.
3. Pending/Unowned fall through to the existing `ResolveOwners` chain (folder-prefix singleton /
   empty).

`GetRegistryForUri` switches from `ResolveOwners(uri).FirstOrDefault()` to
`ResolvePrimaryOwner(uri)`. `ParseAsync` computes the open document's **tags** against the primary
owner's registry — this is what drives both semantic-token coloring and the diagnostics set, so it
must be the primary owner.

> For the log scenario, `Calculator.feature` is physically under `Minimal\`, so the home-project
> rule makes Minimal the primary owner every time — independent of baseline order.

### 3.2 Match-cache key (2B)

Change the match cache from `documentId` to a composite key:

```csharp
readonly record struct MatchSetKey(string DocumentId, ProjectKey Project);
```

- `IBindingMatchService.Store/TryGet/Invalidate` take `MatchSetKey` (or add overloads; keep a
  `InvalidateAllForDocument(string)` for close).
- `FeatureBindingMatchSet` gains a `ProjectKey` (or the key is external). `StepBindingMatch`
  already carries `FeatureDocumentId`; add the owning `ProjectKey` so `FindUsages` can scope.
- **Writer**: on a `BindingRegistryChangedNotification(project)`, the reparse computes and stores
  the match set for **that** `(uri, project)` slot. The N-way fan-out that looks redundant today
  (Anomaly B duplicate reparses) becomes N *distinct, meaningful* writes — one correct match set
  per owner — instead of N stomps on one slot.
- **F14 reader**: `StepReferencesHandler` already knows the queried binding's `.cs` URI →
  `ResolveOwners(csUri)` gives the relevant project(s). `FindUsages(bindingLocation, projectKeys)`
  restricts the scan to match sets whose `ProjectKey` is in that set, eliminating cross-project
  bleed.
- **Diagnostics reader**: `DiagnosticsPublishHandler` reads the **primary owner's** `(uri, P*)`
  slot (single set per URI, per §2).

### 3.3 Invalidation

- **Close** ([`TextDocumentSyncHandler` DidClose](../src/LSP/Reqnroll.IdeSupport.LSP.Server/Handlers/ProtocolHandlers/TextDocumentSyncHandler.cs)):
  invalidate **all** `(uri, *)` slots for the closed URI.
- **Full registry replacement for project P**: invalidate `(*, P)` slots (today `InvalidateAll`
  is the blunt instrument; keying lets it be project-scoped).
- **Project unloaded**: drop `(*, P)`.

---

## 4. Affected components

| Component | 2A | 2B |
|---|---|---|
| `ILspWorkspaceScopeManager` / `LspWorkspaceScopeManager` | add `ResolvePrimaryOwner` | scope-by-project invalidation hooks |
| `BindingRegistryProviderRouter.GetRegistryForUri` | use primary owner | — |
| `IBindingMatchService` / `BindingMatchService` | — | re-key to `MatchSetKey`; `FindUsages` project filter |
| `FeatureBindingMatchSet` / `StepBindingMatch` | — | carry `ProjectKey` |
| `GherkinDocumentTaggerService` (`ParseAsync`, `ScanClosedFileAsync`) | tags vs primary registry | store per-`(uri,project)`; closed-scan needs project arg |
| `BindingRegistryChangedHandler.ReparseOpenFilesAsync` | — | write the notifying project's slot |
| `StepReferencesHandler` (F14) | — | pass owning project keys to `FindUsages` |
| `DiagnosticsPublishHandler` | read primary slot | read primary `(uri, P*)` slot |
| `SemanticTokenService` | no change (fed by primary tags) | no change |

F5 Go-to-Definition (not yet wired) should be designed against `MatchSetKey` from day one.

---

## 5. Phasing & effort

- **Phase 2A (small, ~½ day + tests):** `ResolvePrimaryOwner` + router switch + tagger uses
  primary registry. Kills nondeterminism and wrong-owner squiggles. No cache-shape change.
- **Phase 2B (medium, ~2–3 days + tests):** match-cache re-key, fan-out becomes per-project
  writes, F14 project-scoped `FindUsages`, project-scoped invalidation. Touches Core
  (`BindingMatchService`, `FeatureBindingMatchSet`, `StepBindingMatch`) and several handlers.

Recommend landing 2A first (it's the user-facing win and is independently shippable), then 2B.

---

## 6. Testing

**2A**
- `ResolvePrimaryOwner` returns the home (longest-prefix) project for a multi-owner file.
- External/shared file (outside all folders) → stable ordinal tiebreak; deterministic across
  shuffled baseline order.
- Divergent-registry regression: two owners with different `{Verb}` vs `{string}` bindings; the
  open feature is colored/squiggled against the **home** project's registry.

**2B**
- Multi-owner feature yields one match set per owner (`(uri, A)` and `(uri, B)` both present and
  distinct after both registries discover).
- `FindUsages` for a binding in project A does **not** return steps matched only under project B.
- Closing the URI invalidates every `(uri, *)` slot.
- Full replacement for project P invalidates only `(*, P)`.

---

## 7. Risks / open questions

1. **`ScanClosedFileAsync` has no project context today.** Closed-file scans are driven per
   project by `BindingRegistryChangedHandler`, so the project is available at the call site — the
   signature must gain a `ProjectKey`/`LspReqnrollProject` parameter under 2B.
2. **Memory.** N owners → N match sets per feature. Bounded by (#open or indexed features × #owners);
   negligible for normal solutions, worth a note for very large linked layouts.
3. **Primary-owner stability across edits.** Home-project rule is purely path-based, so it does not
   flip as registries change — good. Only solution-structure changes (add/remove a containing
   project) can change it, which is the correct time to re-evaluate.
4. **Diagnostics for a shared file are necessarily single-project.** A user working the same
   linked file "as project B" still sees primary (project A/home) diagnostics. This is an inherent
   LSP limitation; a future per-project view would need a client-side project selector outside this
   scope.
