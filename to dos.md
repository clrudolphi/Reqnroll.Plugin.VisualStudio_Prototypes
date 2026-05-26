## Porting Action Items

## 0. Prerequisites & Infrastructure
- Establish Reqnroll integration test project scaffold (`Reqnroll.IdeSupport.IntegrationTests`)

## 1. Wizards

### 1a. Cleanup & Removal
- Remove `SpecFlowConfiguration.cs` and `SpecFlowPackageDetector.cs` from `Reqnroll.IDE.Common`
- Remove VB support from `ProjectProgrammingLanguage.cs` and all callers
- Eliminate FluentAssertions from test projects; replace with `Shouldly` or xUnit assertions
- Remove SpecFlow-specific test framework entries from `AddNewReqnrollProjectViewModel` and `AddNewProjectWizardResult`
- Update project template (`Reqnroll.VisualStudio.ProjectTemplate`) to remove SpecFlow/VB artifacts

### 1b. Update & Port
- Update test framework version list in `AddNewReqnrollProjectViewModel` to latest (xUnit 2.9+, NUnit 4.x, MSTest 3.x)
- Port Welcome Wizard: wire `WelcomeDialogViewModel` → `VsWizardDialogService` → VS extension activation event
- Port Prior Version Detection: implement version-check logic in `UpgradeDialogViewModel`; connect to extension startup in `ReqnrollPluginPackage`
- Port unit tests for wizard layer (`WelcomeDialogViewModel`, `UpgradeDialogViewModel`, `ReqnrollProjectTemplateWizard`, `VsWizardDialogService`)

## 2. LSP — Gherkin Language Server

### 2a. Project & Host Setup
- Create `Reqnroll.Gherkin.LanguageServer` project (.NET 10, using `OmniSharp.Extensions.LanguageServer`)
- Create `Reqnroll.Gherkin.LanguageServer.Client` project (VS extension client connector, .NET Framework 4.8.1)
- Port out-of-proc connector service POC into `LanguageServer.Client`
- Register LSP client in `ReqnrollPluginPackage` using VS LSP client APIs; activate on `.feature` file open

### 2b. Internal Architecture
- Create `ProtocolHandlers/` and `InternalHandlers/` folder structure in `LanguageServer` project
- Eliminate C# events as internal collaboration mechanism; replace with:
  - Direct method invocation for low-fan-out, synchronous flows
  - MediatR Request/Response for cross-cutting or expensive deferred operations
- Define `LspTextSnapshot` — immutable document snapshot carrying parse results and version
- Refactor Gherkin parse pipeline: text change → parse → cache `DRTags` on `LspTextSnapshot`
- Research and refactor DR tag parser: separate bind-mapping resolution into its own pipeline stage with independent cache
- Refactor `GetTags` to pull from `LspTextSnapshot`, map to LSP ranges, and respond
- Refactor semantic tag type mapping to match Cucumber/LSP conventions

### 2c. Protocol Handlers (LSP ↔ Client)
- `TextDocumentSync` handler — `textDocument/didOpen`, `didChange`, `didClose` for `.feature` files; triggers Gherkin parse pipeline
- `TextDocumentSync` handler for `.cs` files — triggers C# parsing and step-definition remapping
- `References` handler (`textDocument/references`)
- `DocumentFormatting` handler (`textDocument/formatting`) — driven by `GherkinFormatConfiguration`
- `Hover` handler (`textDocument/hover`)
- `CodeLens` handler (`textDocument/codeLens` + `codeLens/resolve`)
- `Completion` handler (`textDocument/completion` + `completionItem/resolve`)
- `DocumentSymbol` / Outlining handler (`textDocument/documentSymbol`)
- `Rename` handler (`textDocument/rename`)
- `Commenting` handler — map to `textDocument/foldingRange` or custom command

### 2d. Internal Handlers
- `ParseResultEvent` internal handler — receives parsed `LspTextSnapshot`, updates DRTag cache, notifies diagnostics publisher
- `ReqnrollConfigChanged` handler — re-reads `reqnroll.json` / `app.config`, invalidates step-binding cache
- `.editorconfig` changed handler — re-reads formatting config, invalidates format cache

### 2e. Testing
- Generate unit tests for each handler (mock LSP transport)
- Define integration test scenarios (e.g., "step navigates to definition", "missing step shows diagnostic")
- Build Reqnroll acceptance tests for integration scenarios in `Reqnroll.IdeSupport.IntegrationTests`

## 3. Common / Shared Library (`Reqnroll.IDE.Common`)

### 3a. Analytics
- Implement a neutral `NullAnalyticsTransmitterSink` (no-op) in `IDE.Common`
- Implement a `ReqnrollAnalyticsTransmitterSink` backed by OpenTelemetry or Reqnroll's own telemetry pipeline
- Move `AnalyticsTransmitter` composition to use the neutral sink by default; override in VSSDK with `AppInsightsAnalyticsTransmitterSink`
- Remove hard VS dependency from non-VS code paths

### 3b. Gherkin
- Upgrade `Gherkin` NuGet package to latest version across all projects
- Validate `GherkinFormatConfiguration` and `EditorConfiguration` still map correctly after upgrade

### 3c. C# Parser
- Create `Reqnroll.IDE.CSharpParser` project (.NET Standard 2.0)
- Port `CSharpFileDefinition` from current implementation as the base model
- Integrate Code-Grump's Roslyn-based step attribute discovery work as the parsing backend
- Define `ICSharpFileParser`, `IStepDefinitionBinding`, `IStepDefinitionBindingRegistry` interfaces
- Wire parser output into the existing `BindingDiscoveryConfiguration` / project-scope pipeline
- Add unit tests covering attribute patterns: `[Given]`, `[When]`, `[Then]`, `[StepDefinition]`, regex and CucumberExpression styles

## 4. VS Extension Integration (`exp.Reqnroll.Plugin.VisualStudio`)
- Register LSP client in `ReqnrollPluginPackage` — activate on `.feature` file open
- Wire `WelcomeWizard` / `UpgradeWizard` to package activation events
- Replace remaining VSSDK tagger / classifier components with LSP-push equivalents where feasible
- Ensure `VsIdeScope` and `VsProjectScope` remain functional for features not yet covered by LSP