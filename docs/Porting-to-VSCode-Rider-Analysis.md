# Porting Reqnroll.IdeSupport.LSP to VS Code and Rider — Feasibility Analysis

> **Status:** Internal analysis draft — **VS Code section superseded** (2026-07-02) by
> [VSCode-Extension-Implementation-Plan.md](VSCode-Extension-Implementation-Plan.md), which now
> reflects the built extension. The **Rider section (feasibility, R1–R8 task breakdown) is still
> the active reference** — no Rider implementation work has started yet.
> **Date:** 2026-06-29  
> **Audience:** Core team  
> **Author:** Hermes Agent  
> **Related:** [LSP-IDE-Support-Architecture](LSP-IDE-Support-Architecture.md), [LSP-IDE-Support-Feature-Designs](LSP-IDE-Support-Feature-Designs.md)

---

## Table of Contents

1. [Context and Sources](#1-context-and-sources)
2. [Feasibility by Editor](#2-feasibility-by-editor)
3. [Reqnroll.IdeSupport.LSP Feature Inventory](#3-reqnrollidesupportlsp-feature-inventory)
4. [Feature Gaps](#4-feature-gaps)
5. [Editor-Specific Glue Required](#5-editor-specific-glue-required)
6. [Risks](#6-risks)
7. [Recommendations](#7-recommendations)

---

## 1. Context and Sources

This analysis draws on three codebases:

| Source | Role | Notes |
|--------|------|-------|
| **`Reqnroll.IdeSupport`** (this repo) | Target LSP server (`Reqnroll.IdeSupport.LSP.Server`) + current VS client | net10.0 self-contained executable on OmniSharp 0.19.9; protocol-agnostic core in `LSP.Core` (netstandard2.0); Workspace management, MediatR event pipeline |
| **`ThomasHeijtink/Reqnroll.LSP.Plugin`** | PoC — shared GherkinServer + three IDE front-ends | Proven that a single OmniSharp-based server can serve VS Code, Rider, and Visual Studio over stdio. Key empirical findings per-IDE documented. |
| **`reqnroll/Reqnroll.Rider`** | Existing native Rider plugin (Kotlin + C# backend) | Mature product (67★, 615 commits, 15 releases). Features: syntax highlight, navigation, run tests, rename, failing step gutter, step completion, comment support. |

---

## 2. Feasibility by Editor

### 2.1 VS Code — HIGH feasibility

The Thomas Heijtink PoC proves VS Code is the most complete LSP client of the three. The entire feature set operates through `vscode-languageclient` with minimal glue:

- **Server launch:** `ServerOptions` with `stdio` transport; `vscode-languageclient` handles all LSP wire protocol.
- **Document selectors:** `{ language: 'gherkin' }` for `.feature`, `{ language: 'csharp', pattern: '**/Steps/*.cs' }` for step files.
- **Table decorations:** A 150-line `TableHighlightService` applies per-cell text decorations (essential — LSP semantic tokens cannot express per-pipe granularity).
- **Default formatter:** Declared in `package.json` `configurationDefaults` so VS Code routes Format Document to our server.
- **Server path:** Resolved relative to extension directory. The PoC hard-codes a dev path; production bundles the server inside the `.vsix`.

**What the PoC does not explore** (but is needed for production):
- `reqnroll/projectLoaded` and `reqnroll/projectFiles` custom notifications — no MSBuild project system in VS Code, so membership must come from `dotnet msbuild` evaluation or C# Dev Kit.
- TextMate grammar fallback for the activation gap (not in PoC, mentioned in the Architecture doc).
- Semantic token legend mapping via `semanticTokenScopes` / `configurationDefaults`.

**Feasibility score: 9/10.** Low risk; the PoC already demonstrates the core path works.

---

### 2.2 Rider — MODERATE-HIGH feasibility

The PoC proves Rider can host an LSP server via the IntelliJ Platform LSP APIs, but with more caveats than VS Code:

**What works (proven by PoC):**
- `LspServerSupportProvider` + `ProjectWideLspServerDescriptor` start the server on `.feature` / `.cs` open.
- `textDocument/definition` works via the `ReqnrollFeatureDefinitionReferenceProvider` — an `ImplicitReferenceProvider` that makes a **synchronous** LSP `definition` call, bridges the result to IntelliJ's `NavigatableSymbol` model. Without it, Rider cannot navigate from `.feature` text to `.cs` step definitions.
- Semantic tokens: Rider supports the standard LSP pull flow (works without custom code).
- Diagnostics, completion, formatting: standard LSP, work as expected.
- Rename: confirmed working in Rider 2026.1 Nightly (PoC README).
- Code Lens: works via standard LSP.

**What is limited or unproven:**
- **No `onTypeFormatting`:** PoC explicitly calls this out as not working in Rider. Table auto-formatting while typing (F12) requires a different approach.
- **Custom notification transport unproven:** The `reqnroll/projectLoaded`, `reqnroll/projectFiles`, and `reqnroll/projectUnloaded` notifications must flow client→server. The PoC does not explore whether Rider's `ProjectWideLspServerDescriptor` layer reliably forwards custom client-to-server LSP notifications. This is a **medium risk** (the custom notification code is in the server, but the Rider glue layer may need an explicit Kotlin-side LSP extension point to send them).
- **Table cell styling:** Also required per-PoC (LSP semantic tokens are insufficient for per-pipe styling).
- **Server path:** PoC resolves via `ReqnrollServerPathResolver.kt`; production will read the embedded server path.
- **Plugin.xml:** Depends on `com.intellij.modules.lsp`; declares `LspServerSupportProvider`, `ImplicitReferenceProvider`, and `FileType` extensions.

**Platform differences vs. VS Code:**
- Rider's LSP client is built on the `lsp4j` Java library — the same JSON-RPC protocol, so message compatibility is guaranteed.
- Rider uses `GeneralCommandLine` for server process management (instead of VS Code's Node.js spawn).
- The Rider plugin is a JVM Kotlin project with Gradle build; the server is bundled as a platform-specific executable.

**Feasibility score: 7/10.** Proven path exists. Main unknowns: custom notification transport reliability, `onTypeFormatting` workaround, and Kotlin skill requirements for the team.

---

### 2.3 Visual Studio (current LSP client, for baseline)

Skipped in this analysis per the user's framing — the VS client is already built (as `ReqnrollLanguageClient` using VS.Extensibility). The PoC's VS frontend is structurally similar, confirming the approach is correct.

---

## 3. Reqnroll.IdeSupport.LSP Feature Inventory

The server already implements or has design documents for the full feature set planned in the Architecture document. The mapping to the [Feature Design doc](LSP-IDE-Support-Feature-Designs.md) follows:

| ID | Feature | Status (Server) | VS Code | Rider | Notes |
|----|---------|----------------|---------|-------|-------|
| F1 | Gherkin Syntax Highlighting | ✅ Implemented | ✅ Standard pull | ✅ Standard pull | Rider/VS Code use standard `semanticTokens/full` |
| F2 | Binding Discovery | ✅ Implemented | ✅ Std | ✅ Std | Roslyn source + Reflection connector |
| F3 | Gherkin File Diagnostics | ✅ Implemented | ✅ Std | ✅ Std | `textDocument/publishDiagnostics` |
| F4 | Parse Error Display | ✅ Implemented | ✅ Std | ✅ Std | Part of F3 pipeline |
| F5 | Go to Step Definition | ✅ Implemented | ✅ Std | 🔧 ImplicitRefProvider | Rider requires PSI bridge (PoC-confirmed) |
| F6 | Define Steps (Scaffolding) | ✅ Implemented | ✅ Std | ✅ Std | `codeAction` + `workspace/executeCommand` |
| F7 | Keyword Completion | ✅ Implemented | ✅ Std | ✅ Std | `textDocument/completion` |
| F8 | Step Completion | ✅ Implemented | ✅ Std | ✅ Std | `textDocument/completion` + binding context |
| F9 | Document Outline | ✅ Implemented | ✅ Std | ✅ Std | `textDocument/documentSymbol` |
| F10 | Code Folding | ✅ Implemented | ✅ Std | ✅ Std | `textDocument/foldingRange` |
| F11 | Document Auto-formatting | ✅ Implemented | ✅ Std | ✅ Std | `textDocument/formatting` |
| F12 | Table Auto-formatting | ✅ Implemented | 🔧 onType works | ❌ onType broken | Rider doesn't support `onTypeFormatting` |
| F13 | Comment / Uncomment | ✅ Implemented | ✅ Std | ✅ Std | `textDocument/completion` (code action path) |
| F14 | Find Step Definition Usages | ✅ Implemented | ✅ Std | ✅ Std | `textDocument/references` (from .cs) + custom `reqnroll/findStepUsages` (from .feature) |
| F15 | Find Unused Step Definitions | ✅ Implemented | ✅ Std | ✅ Std | Custom `reqnroll/findUnusedStepDefinitions` |
| F16 | Step Rename | ✅ Implemented | ✅ Std | ✅ Std (≥2026.1) | `textDocument/prepareRename` + `textDocument/rename` |
| F17 | Hook Navigation | ✅ Implemented | ✅ Std | ✅ Std | Similar to F5, separate handler |
| F18 | Code Lens (Step Usage Counts) | ✅ Implemented | ✅ Std | ✅ Std | `textDocument/codeLens` + `codeLens/resolve` |
| F19 | New Project/Item Wizards | ❌ VS only | ❌ N/A | ❌ N/A | Not LSP; separate extension concerns |
| F20 | Installation & Upgrade | ❌ design only | ❌ | ❌ | Not LSP; per-IDE distribution concern |
| — | **Project membership (reqnroll/projectFiles)** | 🔧 Design+DTOs | ❌ No producer | ❌ No producer | VS is the only client with a planned producer |
| — | **Failure step highlighting** | ❌ Not in LSP server | ❌ | ❌ | Requires test-event subscription, not LSP |
| — | **Run tests from gutter** | ❌ Not in LSP server | ❌ | ❌ | Requires test-runner integration, not LSP |

**Legend:** ✅ = works via standard LSP with no or minimal client code; 🔧 = requires client-side extension/configuration; ❌ = not supported or not applicable.

---

## 4. Feature Gaps

Features present in the **sample editor integrations** (PoC's VS Code/Rider clients and the existing `Reqnroll.Rider` native plugin) that are **not** present in `Reqnroll.IdeSupport.LSP`.

### 4.1 Gaps vs. the PoC Clients

| Feature | PoC VS Code | PoC Rider | Explanation |
|---------|-------------|-----------|-------------|
| **Table cell decorations** | ✅ 150-line TypeScript class | ✅ Needed (not in PoC Rider but stated as required) | Semantic token protocol cannot express per-cell/per-pipe granularity. Must be client-side. Not in LSP server — by design, not a gap in the server. |
| **`window/showDocument` interception** | ❌ Not needed (VS Code supports it natively) | ❌ Not needed | VS Code and Rider handle `window/showDocument` natively. Visual Studio does not — the PoC's VS client includes a ~340-line `InterceptingDuplexPipe` to intercept and execute `window/showDocument` manually. Not applicable to VS Code/Rider. |

**Conclusion:** The PoC reveals no feature gaps in the LSP server itself that would require server-side changes for VS Code/Rider. The PoC's server code is a strict subset of what Reqnroll.IdeSupport.LSP.Server already implements.

### 4.2 Gaps vs. the Existing Reqnroll.Rider Native Plugin

The existing `Reqnroll.Rider` plugin is a **native Kotlin/IntelliJ PSI plugin**, not an LSP-based plugin. It implements features that the IntelliJ platform exposes through its own extension point system rather than LSP. Porting to an LSP-based architecture means these features must either:

- (a) Be reimplemented as editor-specific glue code in the new Rider plugin, or
- (b) Be implemented server-side if LSP provides a mechanism, or
- (c) Be dropped if they are not viable through LSP.

| Feature | Native Reqnroll.Rider | LSP Approach | Impact |
|---------|----------------------|--------------|--------|
| **Run tests from .feature (gutter icons)** | ✅ Native | ❌ Not LSP reachable | This requires Rider's test-runner API (`GutterIconDescriptor` / `RunLineMarkerContributor`). The server cannot express this. **Must be Rider-specific glue.** ~200-400 lines of Kotlin. |
| **Highlight failing steps after test run** | ✅ Native | ❌ Not LSP reachable | Requires subscribing to Rider's test-runner events and gutter mark mutation (`TestEventPublisher`). LSP does not have a "test failed" diagnostic concept. **Must be Rider-specific glue.** ~200-300 lines of Kotlin. |
| **Code injection (embedded languages in doc strings)** | ✅ Native | ❌ Not LSP reachable | IntelliJ's `LanguageInjectionContributor` allows syntax highlighting embedded XML/SQL in doc strings. LSP semantic tokens could cover this, but it's a large server-side scope. **Low priority.** |
| **Step completion after space (onType completion)** | ✅ Native | ⚠️ Partial | Rider's native plugin uses its own completion model. The PoC confirms Rider's LSP client supports `textDocument/completion` correctly. However, Rider's LSP onType triggering may have gaps (PoC notes step completion after a space was fixed in v2025.3.1 of the native plugin). |
| **Comment/uncomment** | ✅ Native | ✅ LSP (F13) | Already implemented server-side in Reqnroll.IdeSupport.LSP. Can work via LSP if Rider routes the comment action to the LSP server. |
| **Gutter marks for Gherkin structure** | ✅ Native | ⚠️ Code Lens | Rider uses `CodeLens` for usages, but gutter marks for scenario/feature structure are done via IntelliJ's `LineMarkerProvider`. LSP Code Lens may partially cover this, but gutter marks are a different IntelliJ extension point. |
| **Cucumber expression patterns** | ✅ Supported (v2024.3.1) | ✅ Already in LSP server | Reqnroll.IdeSupport.LSP.Core's `StepDefinitionFileParser` and `BindingRegistry` handle both regex and Cucumber expressions through the existing Reqnroll binding model. **No gap.** |
| **`.editorconfig` support** | ✅ Native | ✅ Server-side | The LSP server already reads `.editorconfig` via `FileSystemEditorConfigOptionsProvider`. Rider users get the same settings. **No gap.** |

### 4.3 Gap Summary

| Gap Type | Count | Severity |
|----------|-------|----------|
| **Features that cannot be reproduced via LSP** (run tests, failing steps, code injection, some gutter marks) | 3-4 | Medium–High |
| **Features that need client-side glue code** (table styling, navigation bridge, semantic token mapping) | 3 | Low–Medium |
| **Server-side gaps** (none found) | 0 | N/A |

---

## 5. Editor-Specific Glue Required

This section catalogues every feature that requires client-side code because the editor's built-in LSP client does not support it directly, or where LSP has no protocol mechanism.

### 5.1 Glue Common to Both Editors

| Glue | Reason | Estimated Effort |
|------|--------|------------------|
| **Server launch & lifecycle** | Both editors need to locate the embedded server binary, spawn as a child process over stdio, and manage its lifecycle. | VS Code: ~40 lines TypeScript. Rider: ~60 lines Kotlin. |
| **Document selector registration** | Tell the editor which file types (.feature, .cs) to route to the LSP server. | VS Code: 2 lines in `clientOptions`. Rider: 1 line in `isSupportedFile`. |
| **`reqnroll/projectLoaded` notification** | Send project build properties (output path, TFMs, package refs) to the server. VS Code has no MSBuild project system — must either shell `dotnet msbuild` or integrate with C# Dev Kit. Rider has its own project model and can produce it natively. | VS Code: medium (async MSBuild eval). Rider: small (~30 lines Kotlin calling Rider's project model). |
| **`reqnroll/projectFiles` notification** | Send authoritative file membership (including linked files). **Not yet implemented for any non-VS client.** Same constraints as `projectLoaded` for VS Code. Rider can produce it from its project model (`.csproj` evaluation). | VS Code: medium (same as above). Rider: small (~40 lines Kotlin). |
| **`reqnroll/projectUnloaded` notification** | Signal project removal. | VS Code: trivial. Rider: trivial. |
| **Semantic token legend mapping** | Map the server's `reqnroll.*` custom token types to editor-specific color scopes. | VS Code: `package.json` `semanticTokenScopes` + `configurationDefaults`. Rider: `TextAttributesKey` registration + `getTextAttributesKey` override. |
| **Table cell styling** | LSP semantic tokens cannot express per-pipe/per-cell granularity with correct alignment-relative colors. Both editors need client-side decoration. | VS Code: already exist as ~150-line `TableHighlightService`. Rider: new ~200-300 line Kotlin (or embed the same logic in the Kotlin plugin). |

### 5.2 VS Code-Specific Glue

| Glue | Reason | Effort |
|------|--------|--------|
| **TextMate grammar (fallback)** | Provides basic keyword coloring during the gap between extension activation and the first `semanticTokens/full` response. | ~40 lines JSON (`.tmLanguage.json`); low priority. |
| **`editor.defaultFormatter` + `editor.formatOnType`** | Declared in `package.json` `configurationDefaults`. Already in PoC. | 10 lines in `package.json`. |
| **Server path resolution (dev vs. prod)** | Development: relative path to build output. Production: bundled inside `.vsix` under `server/`. | ~30 lines TypeScript. |

### 5.3 Rider-Specific Glue

| Glue | Reason | Effort |
|------|--------|--------|
| **`ImplicitReferenceProvider` bridge** | **Essential.** Rider cannot cross-language navigate from `.feature` step text to `.cs` methods without a PSI `ImplicitReferenceProvider` that makes a synchronous LSP `definition` call and translates the result to IntelliJ's `NavigationTarget` model. | ~150 lines Kotlin (PoC confirms this works). |
| **`ProjectWideLspServerDescriptor` subclass** | Declares `isSupportedFile`, `createCommandLine`, `getLanguageId`, and `lspGoToDefinitionSupport`. | ~35 lines Kotlin (PoC code is 33 lines). |
| **`LspServerSupportProvider` subclass** | Triggers server start on `.feature` / `.cs` open. | ~20 lines Kotlin. |
| **`plugin.xml` extensions** | Registers `LspServerSupportProvider`, `ImplicitReferenceProvider`, custom `FileType`, and language declaration. | ~30 lines XML. |
| **`ReqnrollFeatureFileType` + `ReqnrollFeatureLanguage`** | Declares the `.feature` file type and its associated language in IntelliJ. | ~20 lines Kotlin each. |
| **`TextAttributesKey` registration for semantic tokens** | Custom mapping from `reqnroll.*` legend names to IntelliJ `TextAttributesKey` definitions with default colors. | ~50 lines Kotlin + color settings page registration. |
| **Table cell decoration** | No VS Code-style `TextEditorDecorationType`; uses `EditorCustomization` / `InlayModel` or `LineMarker`. Technique not yet determined. | Medium (estimate ~200-400 lines Kotlin — larger uncertainty). |
| **Gutter run icons** | `GutterIconDescriptor` / `RunLineMarkerContributor` for running scenarios from `.feature` files. **Not LSP-reachable.** | ~200-400 lines Kotlin. |
| **Failing-step gutter marks** | `TestEventPublisher` subscription + gutter mark mutation. **Not LSP-reachable.** | ~200-300 lines Kotlin. |
| **Build + packaging (Gradle)** | `build.gradle.kts` for building the Rider plugin ZIP. The server must be copied into the plugin output during `prepareSandbox`. Already demonstrated in PoC. | ~80 lines Kotlin DSL (`build.gradle.kts`). |

### 5.4 Total Glue Code Estimate

| Editor | Estimated Kotlin/TypeScript LOC | Notes |
|--------|-------------------------------|-------|
| **VS Code extension** | ~300-400 TypeScript | Mostly tableHighlightService (~150) + extension.ts (~90) + package.json (~50) + server path + fallback grammar. |
| **Rider plugin** | ~800-1300 Kotlin + ~80 lines build.gradle.kts | Larger due to: ImplicitReferenceProvider (~150), gutter features (~400-700), table styling (~200-400), semantic token mapping (~50), and project notifications (~70). Run-test and failing-step features are the largest driver. |

---

## 6. Risks

### R1 · Rider Custom Notification Transport (Medium)

**Risk:** Rider's `ProjectWideLspServerDescriptor` + built-in LSP client may not reliably forward custom client-to-server notifications (`reqnroll/projectLoaded`, `reqnroll/projectFiles`, `reqnroll/projectUnloaded`). The PoC does not test this path.

**Impact:** Without `projectLoaded`, the server cannot discover the project's output assembly path for reflection-based binding discovery. Without `projectFiles`, the server falls back to the folder-prefix membership model, which does not handle linked/excluded files. Degraded but functional (like VS Code's fallback).

**Mitigation:**
- Investigate whether Rider's `LspServer` API supports `sendNotification` for arbitrary LSP methods.
- Fallback: Have the Kotlin glue layer implement a side-channel (e.g., the server also accepts configuration file paths or listens on a local IPC port) for project system data.

### R2 · Rider `onTypeFormatting` Not Supported (Medium)

**Risk:** The PoC explicitly notes Rider does not support `textDocument/onTypeFormatting`. Table auto-formatting as the user types (F12) will not work on Rider.

**Impact:** Rider users lose the table-alignment experience that VS Code and VS users get. Feature non-parity visible in daily editing.

**Mitigation:**
- Implement a Rider-native `TypedActionHandler` or `DocumentListener` that detects table-keypresses (`|`, newline in a table row) and applies formatting directly in the editor. This is what the native `Reqnroll.Rider` plugin likely does.
- Accept the limitation and document it as a known downgrade.

### R3 · Rider Code Lens + References from C# Unproven (Low-Medium)

**Risk:** The PoC's status table shows Code Lens on C# files works in Rider but not VS. The PoC also shows `textDocument/references` on C# files does not work in Rider. However, the PoC's server registers these with a `**/Steps/*.cs` selector — Reqnroll.IdeSupport.LSP registers on all `.cs` files. Rider's built-in LSP may merge or conflict Code Lens from multiple servers (the native C# server + the Reqnroll server).

**Impact:** Step usage Code Lens on binding methods may not appear, and "Find References" on a `[Given]` method may not show feature file usages.

**Mitigation:**
- Verify on Rider 2026.1+ (the native Reqnroll.Rider plugin uses its own PSI-based CodeLens, not LSP CodeLens, so there's no prior data).
- If Rider's LSP client does not merge CodeLens from multiple servers, implement a custom `CodeInsight Contributors` in Kotlin that polls the LSP server for CodeLens data.

### R4 · VS Code Project System Integration (Medium)

**Risk:** VS Code has no first-class MSBuild project system. The `reqnroll/projectLoaded` notification requires the extension to either:
- Shell out to `dotnet msbuild` for every project to get `ProjectProperties` (slow, async).
- Depend on C# Dev Kit's MSBuild integration (adds a hard dependency).

**Impact:** Without project system data, binding discovery falls back to folder-prefix routing (no linked-file support). Reflection-based discovery (post-build) still works via `workspace/didChangeWatchedFiles`.

**Mitigation:**
- Start without `projectLoaded` for VS Code; accept folder-prefix fallback as a v1 limitation.
- Add `dotnet msbuild` evaluation as an async background job in a later version.
- Re-evaluate when C# Dev Kit exposes MSBuild evaluation data as an LSP extension.

### R5 · Maintaining Three IDE Clients Simultaneously (High)

**Risk:** The team must maintain three separate IDE client codebases — VS (C# VS.Extensibility), VS Code (TypeScript), and Rider (Kotlin). Each has different APIs, extension points, build systems, and release cadences. The LSP server is shared, but the glue layers are not, and each needs its own CI, testing, and debugging toolchain.

**Impact:** High maintenance burden. A fix to the semantic token legend (e.g., adding a new `reqnroll.*` type) requires:
1. Updating the server legend.
2. Updating the VS `SemanticTokenClassificationStore` + classifier.
3. Updating the VS Code `semanticTokenScopes` mapping.
4. Updating the Rider `TextAttributesKey` mapping.

**Mitigation:**
- The VS Code and Rider glue layers are substantially thinner than the native `Reqnroll.Rider` plugin (which implements everything natively), so the net maintenance burden is lower than maintaining three separate native plugins.
- Invest in LSP protocol-level spec tests (`.feature` files in the Specs project) that verify the server's response shape — any client that misinterprets a server response is found via spec tests early.
- Consider generating the semantic token legend/mapping from a shared source of truth.

### R6 · .NET Runtime Dependency on Rider (Low)

**Risk:** The LSP server is a .NET self-contained executable. Rider runs on the JVM and hosts its own process model. The Kotlin plugin must locate and execute a native binary, which may have platform/architecture constraints (Rider can run on Linux and macOS; the server binary is currently `win-x64` self-contained).

**Impact:** Multi-platform support requires publishing the server for `linux-x64` and `osx-x64` (and potentially `osx-arm64`). The PoC only tested on Windows.

**Mitigation:**
- Publish the server for all three platforms from CI (`dotnet publish -r <rid> --self-contained`).
- The Rider `GeneralCommandLine` API handles native execution on all platforms — no Kotlin code change needed.

### R7 · Feature Parity Expectations (Medium)

**Risk:** Users of the existing native `Reqnroll.Rider` plugin come to expect its full feature set (run tests from gutter, failing-step highlights). An LSP-based Rider plugin cannot directly reproduce these features via LSP alone, requiring custom Kotlin code.

**Impact:** If users see a "Reqnroll for Rider" LSP-based plugin and find it missing gutter run icons and failing-step highlighting, they may perceive it as inferior to the native plugin, even though the LSP-based plugin offers many features the native plugin does not (e.g., inline step rename, unused step detection, find usages from C#).

**Mitigation:**
- Implement the "must have" native features (run tests, failing steps) as part of the initial Rider plugin scope — do not defer them.
- Clear documentation and Release Notes explaining which features come from the LSP server vs. native integration.

### R8 · Build/CI Complexity (Medium)

**Risk:** The solution currently has one `.slnx` and a single build pipeline. Adding VS Code (npm, vsce) and Rider (Gradle, Kotlin compiler) multiplies the build matrix.

**Impact:** CI becomes more complex. The server must be published for `win-x64` (VS, VS Code, Rider), `linux-x64` (VS Code, Rider), and/or `osx-x64` (VS Code, Rider) before the respective plugin build can bundle it.

**Mitigation:**
- Keep the server publish as a separate CI step upstream of the plugin builds.
- Use GitHub Actions matrix builds with conditional steps.

---

## 7. Recommendations

### 7.1 Start with VS Code (Phase 1)

**Rationale:** Lowest risk, smallest glue code, proven path. The PoC demonstrates ~300 lines of TypeScript is sufficient. The existing `Reqnroll.IdeSupport.LSP.Server` works as-is; no server changes are required for the VS Code client beyond the `--client vscode` flag.

**Scope:**
- Extension activation + LSP client setup (65 LOC TypeScript from PoC).
- Table highlight service (150 LOC TypeScript from PoC).
- `package.json` with language registration, semantic token scopes, and formatter default.
- Custom notification support for `reqnroll/projectLoaded` (MSBuild eval optional in v1).

**Estimated work:** 1 sprint (2 weeks) for a TypeScript-capable developer.

### 7.2 Add Rider with Core Features Only (Phase 2)

**Rationale:** Higher risk but well-understood from PoC. Start with the LSP-driven features (navigation, diagnostics, completion, formatting, rename, code lens) and add the native-only features (run tests, failing steps, gutter marks) as a tracked scope item.

**Scope (core LSP):**
- `plugin.xml`, `LspServerSupportProvider`, `ProjectWideLspServerDescriptor`, `FeatureFileType`, `FeatureLanguage`.
- `ImplicitReferenceProvider` (navigation bridge) — mandatory, PoC code exists.
- Semantic token `TextAttributesKey` mapping.
- Table cell decorations (client-side).
- Project notification support (`projectLoaded` / `projectFiles`).
- Server bundling in `prepareSandbox`.

**Scope (native extras — Phase 2b):**
- Run test gutter icons (`RunLineMarkerContributor`).
- Failing-step highlighting (`TestEventPublisher` → gutter marks).
- Comment/uncomment action hook.

**Estimated work:** 3-4 sprints total (2 for core LSP + 1-2 for native extras) for a Kotlin-capable developer.

### 7.3 Shared Concerns

| Concern | Approach |
|---------|----------|
| **Server `--client` flag** | Extend `Program.cs` to accept `vscode` and `rider` values (alongside existing `visualstudio`). Currently only VS uses the flag for semantic token push mode. VS Code and Rider use standard pull. |
| **Multi-platform publishing** | Add `linux-x64`, `osx-arm64`, `osx-x64` RIDs to the server's `dotnet publish` matrix during CI. |
| **Spec testing** | Extend `tests/LSP/Reqnroll.IdeSupport.LSP.Server.Specs/` with scenarios that simulate VS Code and Rider client capability sets to verify server responses are correct for each client. |
| **Documentation** | Update `docs/LSP-IDE-Support-Architecture.md` §6.1 (VS Code) and §6.3 (Rider) with as-built implementation details as they are built. |

---

## Appendix A · Key Reference Files from Analysis

| File | Purpose |
|------|---------|
| Thomas Heijtink PoC: `README.md` | Full feature matrix across three IDEs; IDE-specific limitations documented |
| Thomas Heijtink PoC: `Program.cs` | Service and handler registration — simpler than Reqnroll.IdeSupport.LSP but same OmniSharp pattern |
| Thomas Heijtink PoC: `ReqnrollLspServerSupportProvider.kt` (Rider) | LSP server start on `.feature`/`.cs` file open |
| Thomas Heijtink PoC: `ReqnrollProjectWideLspServerDescriptor.kt` (Rider) | Command line, file selection, language ID |
| Thomas Heijtink PoC: `ReqnrollFeatureDefinitionReferenceProvider.kt` (Rider) | PSI bridge for cross-language navigation |
| Thomas Heijtink PoC: `extension.ts` (VS Code) | Full VS Code LSP client setup |
| Thomas Heijtink PoC: `tableHighlightService.ts` (VS Code) | Client-side table decoration |
| Thomas Heijtink PoC: `ReqnrollLanguageServerProvider.cs` (VS) | VS.Extensibility LSP host with pipe interceptor for showDocument |
| Reqnroll.Rider: `CHANGELOG.md` | Feature history of the native Rider plugin |
| Reqnroll.IdeSupport: `docs/LSP-IDE-Support-Architecture.md` | Full architecture doc with client specifications |
| Reqnroll.IdeSupport: `docs/LSP-IDE-Support-Feature-Designs.md` | Per-feature design including per-IDE support matrix |

---

## Appendix B · Feature Feasibility Matrix (Detailed)

| # | Feature | LSP Mechanism | VS Code | Rider | Native-only? |
|---|---------|--------------|---------|-------|-------------|
| 1 | Syntax highlighting | `semanticTokens/full` | ✅ | ✅ | No |
| 2 | Parse errors | `publishDiagnostics` | ✅ | ✅ | No |
| 3 | Missing step diagnostics | `publishDiagnostics` | ✅ | ✅ | No |
| 4 | Go to definition | `textDocument/definition` | ✅ | 🔧 ImplicitReferenceProvider | No (but Rider needs glue) |
| 5 | Step scaffolding | `codeAction` + `executeCommand` + `workspace/applyEdit` | ✅ | ✅ | No |
| 6 | Keyword completion | `textDocument/completion` | ✅ | ✅ | No |
| 7 | Step completion | `textDocument/completion` with binding context | ✅ | ✅ | No |
| 8 | Document outline | `textDocument/documentSymbol` | ✅ | ✅ | No |
| 9 | Folding | `textDocument/foldingRange` | ✅ | ✅ | No |
| 10 | Auto-formatting | `textDocument/formatting` | ✅ | ✅ | No |
| 11 | Table on-type formatting | `textDocument/onTypeFormatting` | ✅ | ❌ | Rider needs workaround |
| 12 | Comment/uncomment | `textDocument/codeAction` | ✅ | ✅ | No |
| 13 | Find usages (from C#) | `textDocument/references` | ✅ | ⚠️ Uncertain | May need Rider-side glue |
| 14 | Find usages (from feature) | `reqnroll/findStepUsages` | ✅ | ✅ | No (custom LSP method) |
| 15 | Unused step defs | `reqnroll/findUnusedStepDefs` | ✅ | ✅ | No (custom LSP method) |
| 16 | Step rename | `textDocument/rename` + `prepareRename` | ✅ | ✅ (≥2026.1) | No |
| 17 | Hook navigation | `textDocument/definition` | ✅ | 🔧 Same as F5 | Rider needs glue |
| 18 | Code Lens | `textDocument/codeLens` | ✅ | ✅ | No |
| 19 | Run tests from gutter | N/A | ❌ | 🔧 GutterIconDescriptor | **Yes** — native only |
| 20 | Failing-step gutter marks | N/A | ❌ | 🔧 TestEventPublisher | **Yes** — native only |
| 21 | Code injection | `semanticTokens` | ❌ | 🔧 LanguageInjection | **Yes** — native only |
| 22 | Table cell coloring | Client-side decoration | ✅ TableHighlightService | 🔧 EditorCustomization | **Yes** — client-side only |
