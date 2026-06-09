# Reqnroll LSP-Based IDE Support — Open Questions & Risk Register

> **Status:** Living document — updated as questions are resolved and risks are retired  
> **Audience:** Core team contributors

**Related documents**

| Document | Contents |
|----------|----------|
| [Overview](LSP-IDE-Support-Overview.md) | Scope, goals, high-level architecture, roadmap, release strategy |
| [Architecture & Implementation Reference](LSP-IDE-Support-Architecture.md) | Module design, component inventory, server internals, IDE clients, cross-cutting concerns |
| [Feature Designs](LSP-IDE-Support-Feature-Designs.md) | Per-feature design, sequence diagrams, as-built notes (Appendix A / B) |

---

## Table of Contents

- [Open Questions](#open-questions)
- [Risk Register](#risk-register)

---

## Open Questions

| # | Question | Owner | Status |
|---|----------|-------|--------|
| Q1 | Does Rider's LSP client handle cross-language `textDocument/definition` into `.cs` files without a PSI bridge? (F5, F17) | — | **Resolved**: PSI bridge required. Confirmed by LSP.Plugin PoC (`ReqnrollFeatureDefinitionReferenceProvider`). See [Architecture §6.3](LSP-IDE-Support-Architecture.md#63-rider). |
| Q2 | Does Rider's formatter override `textDocument/formatting`? (F11, F12) | TBD | Needs testing |
| Q3 | What is the minimum supported Rider version for the built-in LSP client? | TBD | Research needed |
| Q4 | Should the Binding Connector be a separate long-running process per workspace, or launched per-request? | TBD | Open |
| Q5 | Per-file Gherkin language overrides via `# language: <code>` first-line header are now handled by the Document Buffer (see [Architecture §5 Workspace Model](LSP-IDE-Support-Architecture.md#workspace-model)). Should we also support a **per-directory** dialect override (e.g., a `reqnroll.json` subdirectory entry), or is per-file + per-project sufficient? | TBD | Open |
| Q6 | Is VS.Extensibility Code Lens support planned for a future VS version, which would remove the VSSDK dependency for F18? | TBD | Monitor VS roadmap |
| Q7 | Is it worth standardizing on a single LSP transport (e.g., stdio) across all three IDE clients, rather than using named pipe for Visual Studio? | — | **Resolved**: all three clients use stdio. |
| Q8 | Should the `Reqnroll.IdeSupport.Common` assemblies be referenced directly by IDE clients (enabling client-side telemetry and logging for installation/upgrade events) rather than having all telemetry flow through the LSP server? | TBD | Open |
| Q9 | How does the LSP server reliably detect that the solution has been rebuilt across all three IDEs? Watching the output assembly path via `workspace/didChangeWatchedFiles` is the current assumption. | TBD | Needs testing |
| Q10 | Should the VisualStudio.* projects be nested under the `clients/` folder alongside the VS Code and Rider clients, or remain in `src/`? | TBD | Open |
| Q11 | Which telemetry architecture should be used? Three options: (a) **Direct HTTP from LSP server** (via `Reqnroll.IdeSupport.Common`) — centralized, but misses pre-server events; (b) **Direct HTTP from each IDE client** — captures installation events, but requires telemetry code in three clients; (c) **LSP `telemetry/event` notification** (server → client) — server fires events, client relays to HTTP endpoint — best of both but requires all three clients to handle the notification. See [Architecture §9 Telemetry](LSP-IDE-Support-Architecture.md#telemetry). | TBD | Open |
| Q12 | Should we plan for debug support for feature files (breakpoints, step-into, etc.) in a future phase? | TBD | Open |
| Q13 | For F14, do the target IDEs reliably dispatch `textDocument/references` to the Reqnroll server vs. the C# server based on caret position (attribute vs. method body)? If not, what is the fallback UX? | **Resolved — dispatch is unreliable in VS.** The VS built-in Find All References does not route to the Reqnroll server; it always resolves via the C# server (finding C# attribute usages or string literals). A custom VS.Extensibility command (`ReqnrollFindStepUsagesCommand`) is required. VS Code and Rider are untested. | Resolved (VS); Needs testing (VS Code, Rider) |
| Q14 | When finding candidate step matches for Step Completion - how sophisticated of a matching algorithm is required? | TBD | Open |
| Q15 | What IPC mechanism connects the LSP server to the out-of-process Binding Connector? Candidates: (a) stdin/stdout child process — simplest, no port conflict; (b) local named pipe — supports long-running Connector reused across builds; (c) localhost TCP with randomized port. Choice also affects Q4 (Connector lifecycle) and security posture ([Architecture §9 Security](LSP-IDE-Support-Architecture.md#security)). | TBD | Open |
| Q16 | What degree of support should be provided for progress support notifications (`$/progress`, `window/workDoneProgress`)? Long-running operations (workspace scan, reflection discovery) are candidates. | TBD | Open |
| Q17 | How should the LSP server associate files with the correct project registry when a `.csproj` **links** files (feature files and/or binding `.cs` files) that live outside the project folder, or **excludes** files inside it? A linked/excluded file is a **many-to-many** relationship (one physical file ↔ zero, one, or several projects) that the folder-prefix membership model cannot express. **Reproduced** in the `Minimal/ExternalReferences` corpus (logs 2026-06-07). | Chris | **Resolved (design)** — authoritative `path → {projects}` index populated by a new optional `reqnroll/projectFiles` notification; folder-prefix retained only as fallback. See [Architecture §5 Workspace Model](LSP-IDE-Support-Architecture.md#workspace-model) and [Feature Designs — Infrastructure](LSP-IDE-Support-Feature-Designs.md#infrastructure-linked-files-and-project-membership) for the full analysis and implementation plan. |
| Q18 | Should the LSP server write to a **local log file** in addition to routing entries via `window/logMessage` to the IDE output channel? A file sink would help users who cannot easily access the IDE output panel. If yes, where should the log file be written and how should it be configured? | TBD | Open |
| Q19 | Should the server support **diagnostic pull** (`textDocument/diagnostic` request, LSP 3.17+) in addition to the current push model (`textDocument/publishDiagnostics`)? Pull allows IDEs to request diagnostics on demand rather than receiving them asynchronously. See [F3](LSP-IDE-Support-Feature-Designs.md#f3--gherkin-file-diagnostics). | TBD | Open |
| Q20 | For step-to-binding navigation (F5), should the server respond to `textDocument/definition` or `textDocument/implementation`? In LSP semantics, a step text is more analogous to an interface/specification (definition) while the binding method is the implementation. The correct choice affects how IDEs route the navigation command. | TBD | Open |
| Q21 | Should the server support `textDocument/documentLink` for step-to-binding navigation? This would render step lines as clickable hyperlinks when the user holds Ctrl and hovers — an alternative or complement to Go to Definition (F5) that requires no keystroke. | TBD | Open |

---

## Risk Register

| # | Risk | Likelihood | Impact | Mitigation |
|---|---|---|---|---|
| R1 | OmniSharp dynamic→static registration for VS semantic tokens requires patching or custom base classes | High | Phase 1 blocker | Spike in Phase 1; custom base classes or OmniSharp patch designed before handler work begins |
| R1a | **(Confirmed)** VS's built-in LSP semantic-token colorizer maps token-type names via a fixed internal table and cannot resolve custom `reqnroll.*` names (they render as plain text); VS also pulls semantic tokens unreliably | High → Resolved | Custom colors absent / intermittent in VS | Resolved: the server **pushes** tokens to the VS client (`reqnroll/semanticTokens`, gated by `--ide visualstudio`) and the VS client drives its own `IClassifier` against `DeveroomClassifications` (see [F1 · Visual Studio](LSP-IDE-Support-Feature-Designs.md#f1--gherkin-syntax-highlighting)). VS Code / Rider are unaffected (they map legend names natively and pull normally). |
| R2 | **(Resolved — VS)** F14 `textDocument/references` dispatch — VS does not route to the Reqnroll server; C# server intercepts unconditionally regardless of caret position | Medium → Resolved (VS) | Custom VS command implemented | `FindStepUsagesCommand` (VS.Extensibility) injects `reqnroll/findStepUsages` over the owned `LspInterceptingPipe`. Surfaces 1 (Extensions menu) and 2 (C# editor context menu) VS-validated 2026-06-09. Surface 3 (Shift+F12 takeover) deferred. VS Code and Rider untested. See [F14 · implementation status](LSP-IDE-Support-Feature-Designs.md#f14--find-step-definition-usages). |
| R3 | Rider formatter overrides LSP `textDocument/formatting` for `.feature` files (F11, F12) | Low–Medium | Formatting degraded on Rider | Testing gate in Phase 3 verification; workaround via Rider formatter configuration if confirmed |
| R4 | OmniSharp.Extensions.LanguageServer goes unmaintained | Low | Major dependency replacement | Fork or migrate to `Microsoft.VisualStudio.LanguageServer.Protocol`; `LSP.Core` is insulated from the framework layer (see [Architecture §10.2](LSP-IDE-Support-Architecture.md#102--omnisharpextensionslanguageserver-vs-alternatives)) |
| R5 | VS.Extensibility does not expose Code Lens API before Phase 4 | High (already known) | VSSDK bridge required for F18 throughout Preview | VSSDK bridge designed and included in Phase 4 plan; monitor VS roadmap (Q6) |
| R6 | IPC mechanism for Binding Connector not yet decided (Q15) | High (open question) | Delays F2 implementation start | Resolve Q15 before Phase 2 begins; treat as Phase 2 pre-condition |
| R7 | ~~Multiple Visual Studio instances open simultaneously → named pipe name collision~~ | ~~Medium~~ | ~~Silent failure for second VS instance~~ | **Retired** — VS uses stdio; no pipe name collision is possible. |
| R8 | Reflection Discovery interrupted by test runner locking the output assembly | Medium | Build-triggered registry update silently fails | Graceful degradation: retain last valid registry, notify user via `window/showMessage` (see [Architecture §9 Error Handling](LSP-IDE-Support-Architecture.md#error-handling-and-resilience)) |
| R9 | Gherkin dialect must be resolved before the first `textDocument/didOpen` is processed | Low | Wrong keyword tokens / completions for non-English projects | Config Loader runs as part of workspace initialization, before the first feature file handler fires |
| R10 | `Reqnroll.LSP.Plugin` PoC patterns diverge from production requirements as design matures | Low | Wasted or conflicting PoC reference | PoC is reference-only; this design document supersedes it wherever they conflict |
