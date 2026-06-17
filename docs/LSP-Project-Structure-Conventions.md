# LSP.Core & LSP.Server — Folder / Namespace / Naming Conventions

## Context

This document recommends a single, consistent organizing convention for the two
LSP projects:

- `src/LSP/Reqnroll.IdeSupport.LSP.Core` — engine logic that is host-agnostic
  (parsing, matching, completions, formatting, rename, etc.).
- `src/LSP/Reqnroll.IdeSupport.LSP.Server` — the OmniSharp-based LSP server that
  wires that engine to the LSP wire protocol (handlers, DTOs, workspace, hosting).

**Good news first:** namespaces already track folders 1:1 in both projects
(file-scoped namespaces, no drift). So this is *not* a namespace-vs-folder
mismatch problem. The problem is that the **folders themselves follow two
competing schemes**, applied at inconsistent depths, so a reader cannot predict
where a class lives or find all the code for one feature.

---

## The core problem: two organizing principles, mixed

Two schemes are in play across the projects:

| Scheme | Meaning | Examples today |
| --- | --- | --- |
| **By layer / responsibility** | folder = the *kind* of thing | `Handlers/`, `Services/`, `Protocol/`, `Notifications/`, `Document/`, `Discovery/` |
| **By feature / function** | folder = the *user-facing capability* | `Editor/Completions/`, `Rename/`, `Matching/`, `Editor/Services/Folding/`, `…/Formatting/`, `…/Commenting/` |

Neither scheme is wrong, but **using both, without a rule for when each
applies, is what hurts discoverability.** The specific symptoms:

### 1. Core: feature folders live at three different depths, with no rationale

The same kind of thing — "a self-contained editor feature" — appears at three
levels:

- Root level: `Diagnostics/`, `Matching/`, `Rename/`
- Under `Editor/`: `Editor/Completions/`, `Editor/Scaffolding/`
- Under `Editor/Services/`: `Editor/Services/Commenting/`, `…/DocumentOutline/`,
  `…/Folding/`, `…/Formatting/`, `…/Parsing/`

Why is `Completions` directly under `Editor/` but `Commenting` two levels deeper
under `Editor/Services/`? Why is `Rename` at the root but `Folding` buried? There
is no principle a reader can infer — the depth is historical accident.

### 2. Core: the `Editor/` and `Editor/Services/` wrappers carry no information

Essentially *everything* in Core is "editor" logic, so `Editor/` doesn't
partition anything. And `Editor/Services/` vs `Editor/` draws an arbitrary line —
`Completions` ships a `CompletionService` too, yet sits outside `Services/`.
`Editor/Services/Parsing/GherkinDocuments/` is **four levels deep** to hold what
is really one concern (the Gherkin parse model).

### 3. Server: one feature is scattered across three folders

Take semantic tokens. Its code lives in:
- `Handlers/ProtocolHandlers/SemanticTokensHandler.cs`
- `Handlers/InternalHandlers/SemanticTokensPushHandler.cs` + `SemanticTokensRefreshHandler.cs`
- `Services/SemanticTokenService.cs` + `ISemanticTokenService.cs` + `ReqnrollSemanticTokens.cs`
- `Protocol/PublishSemanticTokensParams.cs`

Rename, code lens, find-usages, etc. are similarly spread across `Handlers/`,
`Services/`, and `Protocol/`. To understand or change one feature you must visit
3–4 unrelated folders, and `Services/` has become a grab-bag of unrelated
services.

### 4. Same folder name, two different meanings across the projects

`Diagnostics/` in **Core** is the *diagnostics feature* (Gherkin error squiggles:
`DiagnosticsAggregator`, `GherkinDiagnostic`). `Diagnostics/` in **Server**
contains only `LspDeveroomLogger` — *logging infrastructure*, not the diagnostics
feature. Identical name, unrelated content; a reader who learned one project is
actively misled in the other.

### 5. Server: `Protocol/` vs `Notifications/` overlap and leak

- `Notifications/` holds in-process MediatR-style `INotification` records
  (`BindingRegistryChangedNotification`, `MatchCacheChangedNotification`, …).
- `Protocol/` holds LSP *wire* DTOs (`*Params` / `*Response`) **plus** the
  `LspMethodNames` constants **plus** some types that are *also* `INotification`
  (e.g. `ReqnrollProjectFilesParams : INotification`).

So "is this a wire contract or an internal notification?" cannot be answered from
the folder — both folders contain both kinds.

---

## Recommended convention: **feature-first, with a small named shared area**

Pick **feature/capability as the primary axis** in both projects, and confine
"by-kind" grouping to a deliberately small, explicitly-named set of shared
buckets. This is the scheme that scales: a feature = one folder = one namespace,
and cross-cutting foundations are clearly fenced off.

### Rule of thumb

> If code exists to deliver one user-facing capability, it goes in that
> **feature folder** (handler + DTOs + feature service together). If code is a
> foundation that *many* features build on, it goes in a **shared folder** named
> for the concept, never for the layer.

Flatten gratuitous wrappers (`Editor/`, `Editor/Services/`). Keep nesting to **two
levels** below the project root except where a feature genuinely has sub-parts.

---

## Proposed structure — LSP.Core

```
Reqnroll.IdeSupport.LSP.Core/
├─ Gherkin/                     # shared foundation: the parse model + tagging
│  ├─ Parsing/                  #   (was Editor/Services/Parsing/GherkinDocuments)
│  └─ ...                       #   DeveroomGherkin*, DeveroomTag*, ScenarioBlock, …
├─ Documents/                   # shared: snapshot/range abstractions (was Document/)
├─ Bindings/                    # shared domain model (was Discovery/)
│  └─ TagExpressions/
│
├─ Completions/                 # feature  (was Editor/Completions)
│  └─ Matching/
├─ Commenting/                  # feature  (was Editor/Services/Commenting)
├─ DocumentOutline/             # feature  (was Editor/Services/DocumentOutline)
├─ Folding/                     # feature  (was Editor/Services/Folding)
├─ Formatting/                  # feature  (was Editor/Services/Formatting)
├─ Scaffolding/                 # feature  (was Editor/Scaffolding)
├─ Rename/                      # feature  (unchanged)
├─ Matching/                    # feature  (unchanged)
└─ Diagnostics/                 # feature  (unchanged — genuinely the diagnostics feature)
```

Key moves:
- **Delete the `Editor/` and `Editor/Services/` layers.** Promote every feature to
  the root. They are all "editor" features, so the prefix adds nothing.
- **Rename `Discovery/` → `Bindings/`.** Core's `Discovery/` is the binding *domain
  model* (`ProjectBinding`, `ProjectStepDefinitionBinding`, `MatchResult`), not a
  discovery *process* (that lives in Server). `Bindings/` says what it is and stops
  colliding with `Server/Discovery/`, which really does run discovery.
- **Consolidate the parse model under `Gherkin/Parsing/`** instead of the 4-deep
  `Editor/Services/Parsing/GherkinDocuments/`.
- `Document/` → `Documents/` (plural, consistent with other concept folders).

> Naming note: consider dropping the `Deveroom*` prefix on the parse-model types
> (`DeveroomGherkinParser`, `DeveroomTag`, …). It is a legacy brand carried over
> from the old extension and no longer matches the `Reqnroll.IdeSupport` identity.
> Treat this as an optional follow-up — it is a wide rename and orthogonal to the
> folder reorganization.

---

## Proposed structure — LSP.Server

```
Reqnroll.IdeSupport.LSP.Server/
├─ Hosting/                     # Program, ServiceCollectionExtensions,
│  │                            #   LanguageServerOptionsExtensions, ClientIdeContext,
│  │                            #   ProcessHelper, globalUsings
├─ Protocol/                    # SHARED wire contracts only: LspMethodNames +
│  │                            #   DTOs shared by >1 feature. Feature-specific
│  │                            #   DTOs move into the feature folder (below).
├─ Pipeline/                    # in-process INotification records + their internal
│  │                            #   handlers (was Notifications/ + Handlers/InternalHandlers)
├─ Workspace/                   # unchanged (scope/project management)
├─ Discovery/                   # unchanged (the discovery *process*: connectors,
│  └─ AssemblyReflection/       #   Roslyn/connector binding registry providers)
├─ Documents/                   # LSP snapshot/range adapters (was Document/)
├─ Configuration/               # unchanged
├─ Logging/                     # was Diagnostics/ — it only holds LspDeveroomLogger
│
└─ Features/                    # one folder per capability: handler + DTOs + server-only service
   ├─ Completions/              #   GherkinCompletionHandler
   ├─ Definition/               #   FeatureDefinitionHandler, GoToStepDefinitions*, GoToHooks*
   ├─ SemanticTokens/           #   SemanticTokensHandler + SemanticTokenService +
   │                            #     ReqnrollSemanticTokens + PublishSemanticTokensParams
   ├─ CodeLens/                 #   StepCodeLensHandler + RefreshCodeLensParams
   ├─ Rename/                   #   StepRenameHandler + RenameTargets*/SelectRenameTarget*
   ├─ References/               #   StepReferencesHandler, FindStepUsages*
   ├─ FindUnusedStepDefs/       #   FindUnusedStepDefinitionsHandler + its Params/Response
   ├─ Formatting/               #   GherkinFormattingHandler
   ├─ Folding/                  #   FeatureFoldingRangeHandler
   ├─ DocumentOutline/          #   FeatureDocumentSymbolHandler
   ├─ Commenting/               #   CommentToggleHandler
   ├─ CodeActions/              #   FeatureCodeActionHandler
   └─ TextSync/                 #   TextDocumentSyncHandler, WatchedFilesHandler,
                                #     WorkspaceFoldersHandler, DocumentBufferService
```

Key moves:
- **Group by feature under `Features/`.** Each feature folder holds its protocol
  handler, its feature-specific DTOs, and any server-only service. This kills the
  "visit 3 folders to read one feature" problem and empties the `Services/`
  grab-bag.
- **Rename `Diagnostics/` → `Logging/`.** It contains only the logger; the name
  `Diagnostics` wrongly implies the diagnostics feature and clashes with Core.
- **Split `Protocol/` vs `Pipeline/` by transport, not leave them overlapping.**
  `Protocol/` = LSP *wire* contracts + `LspMethodNames` only. In-process
  `INotification` records and their internal handlers go to `Pipeline/`. A type
  that is both (e.g. `ReqnrollProjectFilesParams : INotification`) picks the role
  it actually plays and lives there; do not let it span both folders.
- **Fold `Handlers/InternalHandlers/` into `Pipeline/`** (they *are* the in-process
  notification handlers) and **`Handlers/ProtocolHandlers/` into the feature
  folders.** The `Handlers/` parent then disappears.
- Move hosting/bootstrap files out of the project root into `Hosting/` so the root
  is clean.

> Alternative considered: keep a single flat `Protocol/` for *all* wire DTOs (a
> "contracts together" convention some teams prefer). Rejected as the primary
> recommendation because it perpetuates the feature-scatter; but if the team
> values one-stop protocol review, keeping `Protocol/` whole while still
> consolidating handler+service per feature is a reasonable lighter-touch variant.

---

## Naming conventions (apply uniformly)

- **Namespace = folder path**, file-scoped. Already true; keep it true after the
  move. (`Features/SemanticTokens/` →
  `Reqnroll.IdeSupport.LSP.Server.Features.SemanticTokens`.)
- **Folder names: PascalCase, and a noun for the concept/feature** —
  `SemanticTokens`, not `SemanticTokenStuff`; `Bindings`, not `Discovery` when it's
  a model. Pluralize concept/model buckets (`Documents`, `Bindings`); keep feature
  names as the capability (`Rename`, `Formatting`).
- **Interfaces sit beside their implementation** in the same feature folder
  (`ICompletionService` next to `CompletionService`) — already the norm; preserve
  it.
- **Handlers: name after the LSP message, not the pipeline role** (existing
  convention — see `protocol-handler-naming`). `GherkinCompletionHandler`,
  `SemanticTokensHandler`. The `Features/<X>/` folder now supplies the grouping,
  so the suffix `ProtocolHandler`/`InternalHandler` is unnecessary on the type.
- **DTO suffixes stay meaningful:** `*Params` for request input, `*Response` for
  results, `*Notification` for in-process notifications. Don't tag a wire DTO with
  `Notification` or vice-versa.
- **One public type per file**, file named after the type. (Already followed.)

---

## Suggested phasing (low-risk order)

Because namespaces follow folders, every move is a namespace change and a
mechanical `using` update. Do it in small, independently-buildable steps:

1. **Renames that don't move feature code** (lowest risk, high clarity payoff):
   Server `Diagnostics/` → `Logging/`; Core `Document/` → `Documents/`,
   `Discovery/` → `Bindings/`.
2. **Flatten Core's `Editor/` wrappers** — promote each feature folder to root;
   consolidate the parse model under `Gherkin/Parsing/`.
3. **Server feature-slicing** — introduce `Features/`, move each handler + its DTOs
   + service together, one feature per commit.
4. **Resolve `Protocol/` vs `Pipeline/`** last, once feature DTOs have already
   moved out, so what remains in `Protocol/` is genuinely shared.

Each step: move files, let the IDE/`dotnet` rename the namespace, build, run the
LSP + handler tests, commit. Keep steps separate so review and any revert stay
trivial.

## What explicitly stays the same

- File-scoped namespaces tracking folders.
- One-type-per-file; interface beside implementation.
- Handler-named-after-message convention.
- `Workspace/`, `Configuration/`, Server `Discovery/`, Core `Diagnostics/`,
  `Rename/`, `Matching/` — already well-named for their role.
