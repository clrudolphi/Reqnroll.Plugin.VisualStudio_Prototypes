# Implementation Plan — Authoritative Project-Membership Index (Q17)

**Status:** Milestone A implemented (branch `refactor_to_support_linked_files`)
**Design reference:** [LSP-IDE-Support-Design.md → Q17](LSP-IDE-Support-Design.md#q17--linked-files-and-project-membership--analysis-and-resolution), [Project membership](LSP-IDE-Support-Design.md#project-membership-the-path--projects-index), [Client ↔ Server custom notifications](LSP-IDE-Support-Design.md#client--server-custom-notifications)

This plan turns the chosen Q17 design into concrete code changes across the **LSP server** and the **Visual Studio extension**. VS Code and Rider clients do not yet exist in this repo; the protocol contract here is written so those clients can adopt it later (each simply produces the same `reqnroll/projectFiles` payload from its own project system).

---

## 1. Goal and the invariants we are implementing

Replace folder-prefix membership inference with an **authoritative `path → {(project, TFM)}` index**, populated by a new optional `reqnroll/projectFiles` notification. Every consumer that today asks "which project owns this file?" must move from a single best-guess answer to the index, honouring two invariants:

- **I1 — Membership is conferred only by the index.** Closed-file enumeration is index-driven, never a folder glob. Folder-prefix survives only as a read-only fallback for files no project claims, and never writes a registry or usages/unused result.
- **I2 — Open-state never confers membership or accounting.** An opened-but-unowned file gets registry-independent features only; an opened-but-excluded `.cs` injects bindings into **no** registry.

Plus the lifecycle rule:

- **L1 — Absence is *pending* until a baseline arrives, *excluded* thereafter.** Re-sent on project load, rebuild, and `.csproj` change.

---

## 2. Affected components (map)

| Layer | Component | Change |
|---|---|---|
| Protocol | `ReqnrollProjectFilesParams` (new), `ProjectFileEntry` (new), `ProjectFileRole` (new) | New DTOs |
| Protocol | `Program.cs` `OnNotification` registrations | Register `reqnroll/projectFiles` |
| Server | `ILspWorkspaceScopeManager` / `LspWorkspaceScopeManager` | New index; `GetProjectsForUri` (set); `HandleProjectFilesAsync`; pending/excluded state; keep `GetProjectForUri` as fallback |
| Server | `BindingRegistryProviderRouter.GetRegistryForUri` | Return registries for **all** owning projects |
| Server | `CSharpBindingDiscoveryService.UpdateFromSourceAsync` | Fan out to all owning projects; gate on index (I2) |
| Server | `BindingRegistryChangedHandler` | Index-driven closed-file scan (I1) and open-file reparse; drop folder glob / `StartsWith` |
| Server | `GherkinDocumentTaggerService` + `BindingMatchService` | Per-`(feature, project)` matching + match-cache keying for shared files |
| Core | `IBindingMatchService.FindUsages` / new `FindUnused` | Cross-project union / intersection semantics |
| VS ext | `VsProjectEventMonitor` | Enumerate item membership (CPS/MSBuild), emit `reqnroll/projectFiles` baseline + deltas; hook item/csproj-change events |
| VS ext | `VsUtils` (or new `VsProjectItemEnumerator`) | Resolve `<Compile>`/`<None>` items incl. `Link`, `Remove`, conditions, on-disk paths |
| Tests | LSP.Server.Specs, Core unit tests, VS ext tests | New coverage (see §9) |

---

## 3. Workstream W1 — Protocol & DTOs (LSP server)

**New files** in `src/LSP/Reqnroll.IdeSupport.LSP.Server/Protocol/`:

```csharp
// ReqnrollProjectFilesParams.cs
public sealed class ReqnrollProjectFilesParams
{
    public string ProjectFile { get; set; } = string.Empty;            // index key part 1
    public string TargetFrameworkMoniker { get; set; } = string.Empty; // index key part 2
    public ProjectFilesKind Kind { get; set; } = ProjectFilesKind.Baseline;
    public ProjectFileEntry[] Files { get; set; } = [];
}

public enum ProjectFilesKind { Baseline, Delta }

public sealed class ProjectFileEntry
{
    public string Path { get; set; } = string.Empty;   // absolute on-disk path (links resolved)
    public ProjectFileRole Role { get; set; }          // Feature | Binding
    public bool Added { get; set; } = true;            // delta only; true=add, false=remove
}

public enum ProjectFileRole { Feature, Binding }
```

**Register** in `Program.cs` next to the existing `reqnroll/projectLoaded` block:

```csharp
options.OnNotification<ReqnrollProjectFilesParams>(
    "reqnroll/projectFiles",
    (p, ct) => serverServices!
        .GetRequiredService<ILspWorkspaceScopeManager>()
        .HandleProjectFilesAsync(p, ct));
```

Keying note: `(ProjectFile, TargetFrameworkMoniker)` matches the de-facto key `LspProjectScope.AddOrUpdateProject` already uses (`NormaliseKey(info.ProjectFile)`); the TFM is added so a multi-targeted project's per-TFM conditional membership is distinguishable. The current `LspReqnrollProject` is keyed by `ProjectFile` only — see W2 risk note on multi-TFM.

---

## 4. Workstream W2 — Membership index (LSP server)

**Data structure** in `LspWorkspaceScopeManager`:

```csharp
// path (normalised, OrdinalIgnoreCase) → set of owning project keys
private readonly ConcurrentDictionary<string, HashSet<ProjectKey>> _membership = new(StringComparer.OrdinalIgnoreCase);
// project key → "baseline received" flag (drives pending vs excluded, L1)
private readonly ConcurrentDictionary<ProjectKey, bool> _baselineReceived = new();
```

`ProjectKey` = `(string ProjectFile, string Tfm)` value type.

**New interface members** on `ILspWorkspaceScopeManager`:

```csharp
Task HandleProjectFilesAsync(ReqnrollProjectFilesParams parameters, CancellationToken ct);

/// All projects that the index attributes the URI to. Empty when none.
IReadOnlyCollection<LspReqnrollProject> GetProjectsForUri(DocumentUri uri);

/// Membership resolution state for a URI: Owned / Pending / Unowned.
MembershipState GetMembershipState(DocumentUri uri);
```

**`HandleProjectFilesAsync` behaviour:**
- `Baseline`: replace the project's contribution to `_membership` wholesale (remove the project from every path it previously claimed, then add the new set), set `_baselineReceived[key] = true`. After applying, trigger a re-scan / re-match for that project (it may now own/disown files) — publish the existing `BindingRegistryChangedNotification` for the project (or a lighter membership-changed notification, see W3).
- `Delta`: apply per-entry add/remove against `_membership`. Deltas before a baseline are buffered or dropped (decision: **drop with a warning** — a baseline always follows a project load).

**`GetProjectsForUri`:** look up the normalised path in `_membership`; map keys → live `LspReqnrollProject`. If the path is **not** in the index, return empty (do **not** folder-prefix here — see resolution policy below).

**`GetMembershipState`:**
- path in index → `Owned`
- path not in index, but some covering project has **not** sent a baseline → `Pending`
- path not in index, all covering projects have baselines (or no covering project) → `Unowned`

**Fallback policy (I1).** Keep the existing `GetProjectForUri` (single, folder-prefix) but **demote** it to: "used only when `GetMembershipState == Pending`, or for a project that never sent any `projectFiles` (legacy/again VS Code interim)." Callers choose explicitly; the index is the default. Add an internal helper `ResolveOwners(uri)` that encapsulates: index hit → owners; else pending/no-baseline → folder-prefix singleton; else excluded → empty.

**Multi-TFM note / risk.** `LspProjectScope._projects` is keyed by `ProjectFile` only, so two TFMs of one project currently collapse to one `LspReqnrollProject`. The logs show VS sends one `projectLoaded` per (project, TFM) but they overwrite. Resolving per-TFM membership fully requires keying projects by `(ProjectFile, TFM)`. **Scope decision:** Phase 1 indexes by `ProjectFile` and ignores the TFM dimension (correct for single-targeted projects, which is the common case and the entire `Minimal` corpus); per-TFM keying is a follow-up tracked in §8.

---

## 5. Workstream W3 — Re-gate the consumers (LSP server)

### W3a · `CSharpBindingDiscoveryService.UpdateFromSourceAsync`
Replace the single `GetProjectForUri` (line 30) with `ResolveOwners(uri)` and **loop**:
- For each owning project with a `ConnectorBindingRegistryProvider`, call `ApplyRoslynFileUpdateAsync`.
- If owners is empty *and* state is `Unowned` → log and return (I2: no phantom bindings). If `Pending` → fall back to single folder-prefix owner (best-effort until baseline).

### W3b · `BindingRegistryChangedHandler`
- **Closed-file scan (`ScanAllFeatureFilesAsync`)**: replace `Directory.EnumerateFiles(projectFolder, "*.feature", AllDirectories)` (lines 66-67) with the project's **feature-file set from the index** (`Role == Feature` for that `ProjectKey`). This is the core I1 fix — it both picks up linked features outside the folder and drops excluded ones inside it. Requires the handler to reach the index for the changed project (inject `ILspWorkspaceScopeManager`).
- **Open-file reparse (`ReparseOpenFilesAsync`)**: replace `IsUnderProjectFolder` `StartsWith` (lines 138-145) with index membership: reparse an open buffer iff the changed project owns it. Falls back to folder-prefix only for projects without a baseline.

### W3c · `BindingRegistryProviderRouter.GetRegistryForUri` + tagger matching
This is the deepest change. Today `GetRegistryForUri(uri)` returns **one** registry and `GherkinDocumentTaggerService` stores **one** match set per feature URI (`BindingMatchService._cache` keyed by `DocumentId`). For a feature file owned by N projects this cannot represent "matched in A, unmatched in B."

Two-step approach:
1. **Phase 1 (single-owner correctness):** when a feature has exactly one owner (the overwhelmingly common case, including every excluded/linked case in the corpus once routing is fixed), `GetRegistryForUri` returns that owner's registry — already correct once `GetProjectsForUri` drives selection instead of folder-prefix. No cache-shape change.
2. **Phase 2 (shared-feature correctness):** for a feature owned by >1 project, compute matching against the **union** of owners' registries for diagnostics (a step is unbound only if unbound in *all* owners), and key the match cache by `(DocumentId, ProjectKey)` so per-project usages/unused stay exact. This requires:
   - `BindingMatchService._cache` key → `(string DocumentId, ProjectKey)` (or a composite string).
   - `FeatureBindingMatchSet` carries its `ProjectKey`.
   - `TryGet`/`Invalidate` callers updated.
   
   Phase 2 is only needed for the genuine many-to-many feature-sharing case; gate it behind the index landing and ship Phase 1 first.

### W3d · `GetConfigurationProviderForUri` (dialect/config)
For a multi-owner feature, pick a deterministic owner for `reqnroll.json`/dialect (e.g. first by project name, or flag a conflict). Single-owner: unchanged. Low priority — only affects linked features whose owners disagree on dialect.

### W3e · Open-but-unowned handling (I2)
In the feature sync/parse path: when `GetMembershipState(uri) == Unowned`, skip binding-dependent steps (no unmatched-step diagnostics, no step→binding) and optionally publish a single informational diagnostic ("not included in any project; step validation unavailable"). Registry-independent features (semantic tokens, parse errors, folding, formatting, symbols) run normally. `Pending` behaves as today (best-effort) to avoid startup flicker.

---

## 6. Workstream W4 — VS extension manifest production

### W4a · Item enumeration
EnvDTE `ProjectItems` is unreliable for SDK-style projects, glob includes, `Remove`, conditions, and link on-disk paths. Add a `VsProjectItemEnumerator` that obtains evaluated items from **CPS** (`IVsBrowseObjectContext` → `UnconfiguredProject`/`ConfiguredProject` → `ProjectInstance`/project-subscription dataflow) or, as a pragmatic first cut, an out-of-band **MSBuild evaluation** of the `.csproj` (read `Compile` and `None` items, honour `Link`/`LinkBase`, resolve to absolute paths, exclude items removed by `Remove`/false `Condition`). Output: two lists (feature paths, binding `.cs` paths), already filtered to Reqnroll-relevant files.

> The CPS path is preferred for live accuracy and change events; the MSBuild-evaluation path is acceptable as a v1 that runs on load/rebuild/csproj-change. Decide during W4 based on CPS API ergonomics.

### W4b · Notification emit
In `VsProjectEventMonitor`, after each `TrySendProjectLoadedAsync`, add `TrySendProjectFilesAsync(project, kind: Baseline)` that serialises `ReqnrollProjectFilesParams` and sends `reqnroll/projectFiles` over the existing `_pipe`. Wire into `SendInitialProjectsAsync`, `OnProjectAdded`, and `OnBuildDone` (baseline re-send).

### W4c · Change events (L1)
Subscribe to project item-add/remove and `.csproj`-change:
- DTE: `_dte.Events.get_DocumentEvents()` / `ProjectItemsEvents` (`ItemAdded`/`ItemRemoved`/`ItemRenamed`) for coarse deltas; or
- CPS dataflow subscription for precise item changes.
Emit `kind: Delta` messages on change; emit a fresh `Baseline` on `.csproj` save. This restores ownership when a user re-includes a file in the editor.

### W4d · `ReqnrollProjectLoadedParams` unchanged
`projectLoaded` keeps its current cheap EnvDTE payload — deliberately not extended (the whole point of the separate message). No change to `VsUtils.GetOutputAssemblyPath` etc.

---

## 7. Workstream W5 — F14 / F15 cross-project semantics (Core)

- **F14 `FindUsages`** (`BindingMatchService.FindUsages`, already global over `_cache`): correct as-is **once** every feature file is matched against its owning project(s)' registries (W3b/W3c). Add a regression test for the linked-binding case (binding in A, linked into B, used by a feature in B → usage found).
- **F15 `FindUnused`** (not yet implemented; Phase 4): implement as "binding has zero usages across the union of match sets of every project that includes the binding's `.cs`." With Phase-1 single-owner matching this reduces to the global scan; with Phase-2 keying it becomes the intersection described in the design. Implement against the index so it never reports a linked-and-used binding as unused.

---

## 8. Sequencing & phasing

```
W1 (protocol DTOs) ─┬─> W2 (index in scope manager) ─┬─> W3a (Roslyn fan-out)
                    │                                 ├─> W3b (closed/open scan)
                    │                                 ├─> W3c-Phase1 (single-owner routing)
                    │                                 └─> W3e (unowned handling)
                    └─> W4 (VS ext manifest) ─────────┘  (W4 can proceed in parallel once W1 lands)

Later: W3c-Phase2 (match-cache keying) ─> W5 (F14 regression, F15) ; per-TFM project keying
```

**Milestone A (correctness for the corpus):** W1 + W2 + W3a/b/c-Phase1/e + W4a/b. At this point linked and excluded files route correctly for diagnostics and discovery; the `Minimal/ExternalReferences` reproduction passes.

**Milestone B (full many-to-many):** W3c-Phase2 + W5 + per-TFM keying. Needed only for genuinely shared feature files and exact per-project usages/unused.

---

## 9. Testing

**Core / Server unit (xUnit):**
- `LspWorkspaceScopeManager`: baseline apply, delta add/remove, `GetProjectsForUri` returns multiple owners, pending→excluded transition, fallback when no baseline.
- `CSharpBindingDiscoveryService`: fan-out patches N providers; unowned `.cs` patches none.
- `BindingRegistryChangedHandler`: closed-file scan uses index set (linked feature in, excluded feature out).

**LSP.Server.Specs (protocol simulation):** drive `reqnroll/projectLoaded` + `reqnroll/projectFiles` for a 2-project linked layout mirroring `Minimal/ExternalReferences`; assert diagnostics for the linked feature resolve against the linking project, and an excluded-but-open feature gets no unmatched-step diagnostics.

**VS extension:** unit-test `VsProjectItemEnumerator` against fixture `.csproj` files (link, `Remove`, conditional include, multi-TFM); assert on-disk paths and exclusions.

**Regression corpus:** keep `Minimal/ExternalReferences` as the manual end-to-end check; capture an inspector-log assertion that `reqnroll/projectFiles` carries the linked feature under `ExternalReferences`.

---

## 10. Risks & open sub-questions

1. **Match-cache keying (W3c-Phase2)** is the largest structural change; deferring it is safe only while shared feature files have a single *effective* owner. Validate that assumption against real multi-project solutions before declaring Milestone A "done."
2. **Per-TFM project identity**: `LspReqnrollProject` keyed by `ProjectFile` only. Multi-targeted projects with TFM-conditional membership need `(ProjectFile, TFM)` keys end-to-end. Tracked as a follow-up; not required for single-TFM projects.
3. **CPS vs MSBuild-evaluation in VS** (W4a): CPS gives live change events but more API surface; MSBuild-eval is simpler but coarser (re-evaluate on csproj/build). Pick during W4.
4. **Ordering** of `projectLoaded` vs `projectFiles`: handler must tolerate either order (buffer deltas pre-baseline; pending state covers the gap).
5. **VS Code / Rider** producers are out of scope here but must emit the identical payload; the server contract (W1/W2) is client-agnostic by construction.

---

## 11. Backward compatibility / fallback

A client that never sends `reqnroll/projectFiles` leaves every project in the "no baseline" state, so `ResolveOwners` falls back to folder-prefix everywhere — i.e. **today's behaviour**. The index is therefore strictly additive: shipping the server changes ahead of any client change is safe, and each client can adopt the notification independently.
