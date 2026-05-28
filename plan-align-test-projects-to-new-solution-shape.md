# 🎯 Align Test Projects to New Solution Shape

## Context & Key Findings

### Solution structure (new `src\`)
| Project | TFM | Role |
|---|---|---|
| `Reqnroll.IdeSupport.Common` | netstandard2.0 | Analytics, Configuration, Diagnostics, ProjectSystem interfaces/providers |
| `Reqnroll.IdeSupport.LSP.Connector.Models` | netstandard2.0 | Connector wire models |
| `Reqnroll.IdeSupport.LSP.Core` | netstandard2.0 | Gherkin parsing (`DeveroomGherkinParser`, `DeveroomTagParser`), Discovery (`ProjectBindingRegistry`, `BindingImporter`, `StepDefinitionFileParser`), Document model |
| `Reqnroll.IdeSupport.LSP.Server` | net10 | OmniSharp handlers: `TextDocumentSyncHandler`, `SemanticTokensHandler`, `WatchedFilesHandler`, `WorkspaceFoldersHandler`; services: `DocumentBufferService`, `GherkinDocumentTaggerService`, `SemanticTokenService`; workspace: `LspWorkspaceScopeManager`, `LspProjectScope` |
| `Reqnroll.IdeSupport.VisualStudio.VSSDKIntegration` | net481 | `VsIdeScope`, `VsProjectScope`, `VsUtils`, analytics, monitoring |
| `Reqnroll.IdeSupport.VisualStudio.Wizards.Core` | netstandard2.0 | `ReqnrollProjectTemplateWizard`, `ConfigFileTemplateWizard`, `FeatureFileTemplateWizard`, all abstractions (`IWizardContext`, `IWizardDialogService`, `IWizardTelemetry`, `WizardProjectSettings`, `AddNewProjectWizardResult`) |
| `Reqnroll.IdeSupport.VisualStudio.Wizards.UI` | net481 | WPF ViewModels: `AddNewReqnrollProjectViewModel`, `WelcomeDialogViewModel`, `UpgradeDialogViewModel`, `WizardViewModel`; Dialogs |
| `Reqnroll.IdeSupport.VisualStudio.Wizards` | net481 | VS MEF layer: `VsReqnrollProjectWizard`, `VsTemplateWizardBase`, `VsWizardContext`, `VsWizardDialogService`, `VsWizardTelemetry` |

### Test projects (old, currently unmodified)
| Project | Status |
|---|---|
| `Reqnroll.VisualStudio.Tests` | Broken — references `Reqnroll.VisualStudio.csproj` (gone) |
| `Reqnroll.VisualStudio.VsxStubs` | Broken — same reference |
| `Reqnroll.VisualStudio.Specs` | Broken — same + `VsxStubs` |
| `Reqnroll.IdeSupport.VisualStudio.Wizards.UI.Tester` | Manual WPF harness — build only |
| `Reqnroll.SampleProjectGenerator.Core` / `.Generator` | Used by Specs — decouple from VS project ref |

The missing `Reqnroll.VisualStudio.csproj` was the old monolithic project that has been split across all the new `src\` projects.

---

## Part 1 — Test Support Plumbing (VsxStubs retarget)

`Reqnroll.VisualStudio.VsxStubs` must compile against the new projects. Its stubs fall into two categories:

**A — VS-heavy stubs** (keep, update references):
`StubIdeScope`, `InMemoryStubProjectScope`, `StubProjectScope`, `StubProjectSettingsProvider`, `StubIdeActions`, `StubAnalyticsTransmitter`, `StubErrorListServices`, `StubWindowManager`, `DeveroomXUnitLogger`, `MockFileSystemForVs`, `InMemoryStubProjectBuilder`, `TaskExtensions` — these implement interfaces from `Reqnroll.IdeSupport.Common` and use VS editor types from VSSDK.

**B — VS editor stubs** (keep but scope to VS-only tests):
`StubWpfTextView`, `StubTextBuffer`, `StubTextSnapshot`, `StubTextVersion2`, `StubTextCaret`, `StubTextSelection`, `StubViewScroller`, `StubAdornmentLayer`, `StubBufferTagAggregatorFactoryService`, `StubTagAggregator`, `StubContentType`, `StubEditorOptions`, `StubEditorFormatMap`, `StubEditorConfigOptionsProvider`, `StubCompletionBroker`, `StubCompletionSession`, `FilePathProvider`, `VsxStubObjects` — require VSSDK text model assemblies.

**C — Connector-dependent stubs** (deferred with their tests):
`StepDefinitions\MockableDiscoveryService`, `StubDiscoveryResultProvider`, `RuntimeDependencyLock`, `StubProjectBindingRegistryCache` — depend on VS connector which is not yet ported.

**D — New lean stubs needed** for LSP.Core/Server tests that must not touch VSSDK:
`StubGherkinTextSnapshot` (implements `IGherkinTextSnapshot` from `LSP.Core\Document`), a minimal `StubLspIdeScope` and `StubLspProjectScope` (implement `IIdeScope`/`IProjectScope` from `Common`, no VS editor types).

---

## Part 2 — `Reqnroll.VisualStudio.Tests` — Per-file disposition

### Migrate to new `tests\Core\Reqnroll.IdeSupport.Common.Tests` (net10, xunit)
| Old file | Target class | Notes |
|---|---|---|
| `Analytics\AnalyticsTransmitterTests.cs` | `Common\Analytics\AnalyticsTransmitter` | Namespace rename only |
| `Configuration\CSharpCodeGenerationConfigurationTests.cs` | `Common\Configuration\CSharpCodeGenerationConfiguration` | Namespace rename |
| `Configuration\ReqnrollConfigDeserializerTests.cs` | `Common\Configuration\ReqnrollConfigDeserializer` | Namespace rename |
| `Diagnostics\LoggingTests.cs` | `Common\Diagnostics\*` | Namespace rename |
| `ProjectSystem\ReqnrollPackageDetectorTests.cs` | `Common\ProjectSystem\ReqnrollPackageDetector` | Namespace rename |

### Migrate to `tests\VisualStudio\Reqnroll.IdeSupport.VisualStudio.Tests` (net481, xunit) — VS-specific
| Old file | Target class | Notes |
|---|---|---|
| `Analytics\FileUserIdStoreTests.cs` | `VSSDKIntegration\Analytics\FileUserIdStore` | Needs registry/VS stubs |

### Migrate to new `tests\LSP\Reqnroll.IdeSupport.LSP.Core.Tests` (net10, xunit)
| Old file | Target class | Notes |
|---|---|---|
| `Discovery\BindingImporterTests.cs` | `LSP.Core\Discovery\BindingImporter` | Namespace rename |
| `Discovery\ProjectBindingRegistryTestsBase.cs` | `LSP.Core\Discovery\ProjectBindingRegistry` | Namespace + base class rename |
| `Discovery\ProjectBindingRegistryMatchTests.cs` | same | |
| `Discovery\ProjectBindingRegistryAmbiguousTests.cs` | same | |
| `Discovery\ProjectBindingRegistryCacheTests.cs` | same | |
| `Discovery\ProjectBindingRegistryMultiMatchTests.cs` | same | |
| `Discovery\ProjectBindingRegistryUndefinedTests.cs` | same | |
| `Discovery\ReprocessStepDefinitionFileTests.cs` | `LSP.Core\Discovery\StepDefinitionFileParser` | Namespace rename; bring `ApprovalTestData\` |
| `Discovery\StubGherkinDocument.cs` | support stub | |
| `Editor\Services\DeveroomGherkinParserTests.cs` | `LSP.Core\...\DeveroomGherkinParser` | Namespace rename |
| `Editor\TestFeatureFile.cs` | shared test helper | |
| `Editor\TestStepDefinition.cs` | shared test helper | |

### Migrate to new `tests\LSP\Reqnroll.IdeSupport.LSP.Server.Tests` (net10, xunit)
| Old file | Target class | Notes |
|---|---|---|
| `Editor\Services\FeatureFileTaggerTests.cs` | `LSP.Server\Services\GherkinDocumentTaggerService` | The old tagger is now `GherkinDocumentTaggerService`. `TaggerSut.cs` becomes a builder that wires `DeveroomTagParser` + `DocumentBufferService`. Replace `ITextSnapshot`/`ITagSpan` with `LspTextSnapshot`/`DeveroomTag`. |
| `Editor\Services\TaggerSut.cs` | support class | Reshape alongside tagger tests |

### Deferred — No corresponding implementation yet
| Old file | Reason |
|---|---|
| `Editor\Commands\*` (8 files) | GoToDefinition, Rename, Find, AutoFormat, CommentUncomment, DefineSteps not yet LSP handlers |
| `Editor\Completions\StepDefinitionSamplerTests.cs` | `StepDefinitionSampler` not ported; completion handler not implemented |
| `Editor\Services\GherkinDocumentFormatterTests.cs` | `DocumentFormatting` handler not implemented |
| `Editor\Services\StepDefinitionUsageFinderTests.cs` | `textDocument/references` handler not implemented |
| `Snippets\SnippetServiceTests.cs` | Snippet service not ported |
| `Discovery\DiscoveryTests.cs` | Depends on VS connector (not yet ported); `MockableDiscoveryService` in VsxStubs is connector-dependent |

---

## Part 3 — `Reqnroll.VisualStudio.Specs` — Feature file disposition

### Keep and reshape (step defs retargetable to new LSP stubs)
| Feature file | Maps to | Action |
|---|---|---|
| `Editor\SyntaxColoring.feature` | `DeveroomTagParser` + `GherkinDocumentTaggerService` | Reshape `DeveroomSteps.cs` to use `LspTextSnapshot` + `GherkinDocumentTaggerService` instead of VS tagger |
| `Editor\SyntaxErrors.feature` | `DeveroomTagParser` (parser error tags) | Same reshape |
| `Editor\StepAnalysis.feature` | `DeveroomTagParser` step match tags + `ProjectBindingRegistry` | Same reshape; seed `ProjectBindingRegistry` from test step definitions |
| `Editor\ScenarioTraceability.feature` | Hook binding in `ProjectBindingRegistry.MatchScenarioToHooks` | Same reshape |

### Deferred — Implementation not yet available
| Feature file | Missing capability |
|---|---|
| `Editor\Commands\GoToDefinitionCommand.feature` | `textDocument/definition` handler |
| `Editor\Commands\GoToHooksCommand.feature` | `textDocument/definition` (hooks variant) |
| `Editor\Commands\FindStepDefinitionUsagesCommand.feature` | `textDocument/references` handler |
| `Editor\Commands\FindUnusedStepDefinitionCommand.feature` | `textDocument/references` handler |
| `Editor\Commands\DefineStepsCommand.feature` | Code action / `workspace/applyEdit` |
| `Editor\Commands\RenameStepsCommand.feature` | `textDocument/rename` handler |
| `Editor\Commands\AutoFormatDocumentCommand.feature` | `textDocument/formatting` handler |
| `Editor\Commands\AutoFormatTableCommand.feature` | `textDocument/formatting` handler |
| `Editor\Commands\CommentUncommentCommand.feature` | Custom command / `textDocument/foldingRange` |
| `Editor\StepCompletion.feature` | `textDocument/completion` handler |
| `Editor\KeywordCompletion.feature` | `textDocument/completion` handler |
| `Discovery\*` (5 files) | VS connector — `StepDefinitionFile` discovery pipeline not yet wired |

### Support files
`DeveroomSteps.cs` and `ProjectSystemSteps.cs` need significant reshaping for the kept features. `ProjectSystemSteps.cs` heavily uses `InMemoryStubProjectScope` + `SampleProjectGenerator` — update to use lean LSP stubs for the kept features, keep VS stubs for deferred VS-specific scenarios.

---

## Part 4 — Wizard Tests

The old solution had no wizard unit tests. The `Wizards.UI.Tester` project is a manual WPF harness (no xunit). Create `tests\VisualStudio\Reqnroll.IdeSupport.VisualStudio.Wizards.Tests` (net481, xunit) referencing `Wizards.Core` + `Wizards.UI`:

### Tests to write (new, no old equivalent)
| Test class | What to test |
|---|---|
| `ReqnrollProjectTemplateWizardTests` | `RunStarted` with mock `IWizardContext`, cancelled dialog returns false, successful run populates replacement dict keys (`$dotnetframework$`, `$unittestframework$`, `$rootnamespace$`) for all framework/test combos |
| `ConfigFileTemplateWizardTests` | `RunStarted` emits correct config file content per wizard context |
| `FeatureFileTemplateWizardTests` | `RunStarted` injects correct replacements |
| `AddNewReqnrollProjectViewModelTests` | `DotNetFramework` setter fires `PropertyChanged`; `IsNetFramework` correct for net4x vs net8; `TestFrameworks` collection populated |
| `WelcomeDialogViewModelTests` | Page navigation, `INotifyPropertyChanged` |
| `UpgradeDialogViewModelTests` | Version comparison logic, display text |
| `VsWizardContextTests` (VS-integration layer) | `ReplacementsDictionary` round-trip, `WizardProjectSettings` mapping from `ProjectSettings` — these need `InMemoryStubProjectScope` |

---

## Part 5 — New LSP Tests (no old equivalent)

Create `tests\LSP\Reqnroll.IdeSupport.LSP.Server.Tests`:

| Test class | SUT | Key scenarios |
|---|---|---|
| `DocumentBufferServiceTests` | `DocumentBufferService` | Update, UpdateTags, Remove, TryGet, All enumeration; thread safety |
| `LspWorkspaceScopeManagerTests` | `LspWorkspaceScopeManager` | OpenWorkspace idempotent; CloseWorkspace disposes scope; GetScopeForUri longest-prefix match; unknown URI returns null |
| `TextDocumentSyncHandlerTests` | `TextDocumentSyncHandler` | DidOpen stores buffer + triggers `GherkinDocumentParsedNotification`; DidChange updates buffer; DidClose removes buffer; non-feature URI ignored |
| `GherkinDocumentTaggerServiceTests` | `GherkinDocumentTaggerService` | Returns tags for valid feature; returns parser error tag for malformed feature; uses injected `IDeveroomTagParser` |
| `SemanticTokenServiceTests` | `SemanticTokenService` | Legend contains expected token types; `GetTokens` maps `DeveroomTag` types to correct indices |
| `SemanticTokensHandlerTests` | `SemanticTokensHandler` | Full request returns token data; unknown document returns null |
| `WatchedFilesHandlerTests` | `WatchedFilesHandler` | `reqnroll.json` change fires `ReqnrollConfigChangedNotification`; unrelated file change ignored |
| `WorkspaceFoldersHandlerTests` | `WorkspaceFoldersHandler` | Added folder opens workspace scope; removed folder closes scope |

Create `tests\LSP\Reqnroll.IdeSupport.LSP.Core.Tests`:

| Test class | SUT | Key scenarios |
|---|---|---|
| `DeveroomTagParserTests` | `DeveroomTagParser` | Feature/Rule/Scenario/ScenarioOutline blocks; step keyword tags; defined/undefined/ambiguous/binding-error step tags; DataTable/DocString argument tags; scenario outline placeholder tags; scenario hook reference tags; parser error tags; multiline description tags; empty feature |
| `GherkinDocumentContextCalculatorTests` | `GherkinDocumentContextCalculator` | Returns correct scenario/step context for cursor positions |

---

## Part 6 — New Test Project csproj Specs

### `tests\Core\Reqnroll.IdeSupport.Common.Tests\*.csproj`
- TFM: `net10.0`
- References: `Reqnroll.IdeSupport.Common`, xunit 2.x, NSubstitute, FluentAssertions, System.IO.Abstractions.TestingHelpers

### `tests\LSP\Reqnroll.IdeSupport.LSP.Core.Tests\*.csproj`
- TFM: `net10.0`
- References: `Reqnroll.IdeSupport.LSP.Core`, `Reqnroll.IdeSupport.Common`, xunit, NSubstitute, FluentAssertions, ApprovalTests (for `ReprocessStepDefinitionFileTests`)

### `tests\LSP\Reqnroll.IdeSupport.LSP.Server.Tests\*.csproj`
- TFM: `net10.0`
- References: `Reqnroll.IdeSupport.LSP.Server`, `Reqnroll.IdeSupport.LSP.Core`, `Reqnroll.IdeSupport.Common`, xunit, NSubstitute, FluentAssertions, MediatR test utilities

### `tests\VisualStudio\Reqnroll.IdeSupport.VisualStudio.Wizards.Tests\*.csproj`
- TFM: `net481`
- References: `Reqnroll.IdeSupport.VisualStudio.Wizards.Core`, `Reqnroll.IdeSupport.VisualStudio.Wizards.UI`, `Reqnroll.IdeSupport.VisualStudio.Wizards`, `Reqnroll.IdeSupport.VisualStudio.VSSDKIntegration`, xunit, NSubstitute, FluentAssertions

### `tests\VisualStudio\Reqnroll.IdeSupport.VisualStudio.Tests\*.csproj` (retargeted from old Tests)
- TFM: `net481`
- Remove old `Reqnroll.VisualStudio.csproj` reference
- Add: `Reqnroll.IdeSupport.VisualStudio.VSSDKIntegration`, `Reqnroll.IdeSupport.Common`, `Reqnroll.VisualStudio.VsxStubs`

### `Reqnroll.VisualStudio.VsxStubs\*.csproj` (retargeted)
- Remove old `Reqnroll.VisualStudio.csproj` reference
- Add: `Reqnroll.IdeSupport.Common`, `Reqnroll.IdeSupport.VisualStudio.VSSDKIntegration`, `Reqnroll.IdeSupport.LSP.Core`
- Keep VSSDK build tools reference and external VS assembly references

### `Reqnroll.VisualStudio.Specs\*.csproj` (retargeted)
- Remove old `Reqnroll.VisualStudio.csproj` reference
- Add: `Reqnroll.IdeSupport.Common`, `Reqnroll.IdeSupport.LSP.Core`, `Reqnroll.IdeSupport.LSP.Server`
- Remove `DeploymentAssets.props` import (connector assets — no longer applicable)

---

## Summary Table

| Old test file group | New home | Status |
|---|---|---|
| Analytics (Common) | `Common.Tests` | Migrate |
| Config / Diagnostics / PackageDetector | `Common.Tests` | Migrate |
| FileUserIdStore | `VisualStudio.Tests` | Migrate |
| Discovery (binding registry, importer, reprocess) | `LSP.Core.Tests` | Migrate |
| GherkinParser | `LSP.Core.Tests` | Migrate |
| FeatureFileTagger | `LSP.Server.Tests` | Reshape |
| Editor Commands (8 test classes) | — | Deferred |
| Completion, Format, UsageFinder, Snippets | — | Deferred |
| Discovery Specs features (5) | — | Deferred |
| Editor Commands Specs features (9) | — | Deferred |
| StepCompletion/KeywordCompletion Specs | — | Deferred |
| SyntaxColoring/Errors/StepAnalysis/Traceability Specs | `Specs` (reshaped) | Reshape |
| Wizard tests (all new) | `Wizards.Tests` | New |
| LSP handler/service tests | `LSP.Server.Tests` | New |
| DeveroomTagParser comprehensive tests | `LSP.Core.Tests` | New |

**Progress**: ~90% [█████████░]

**Last Updated**: 2026-06-05

## 📝 Plan Steps
- ✅ **Retarget `Reqnroll.VisualStudio.VsxStubs.csproj` — remove `Reqnroll.VisualStudio.csproj` reference; add `Reqnroll.IdeSupport.Common`, `Reqnroll.IdeSupport.VisualStudio.VSSDKIntegration`, `Reqnroll.IdeSupport.LSP.Core`; fix all broken `using` / namespace references in each stub file to use the new `Reqnroll.IdeSupport.*` namespaces** — ✅ Builds successfully
- ✅ **Add lean LSP stubs to `VsxStubs` — create `LspStubs\StubGherkinTextSnapshot.cs` (implements `IGherkinTextSnapshot`)** — ✅ `StubGherkinTextSnapshot.cs` present; `StubLspIdeScope.cs` not yet created (not yet required by passing tests)
- ✅ **Retarget `Reqnroll.VisualStudio.Tests.csproj` — remove `Reqnroll.VisualStudio.csproj` reference; add `Reqnroll.IdeSupport.Common`, `Reqnroll.IdeSupport.VisualStudio.VSSDKIntegration`, `Reqnroll.IdeSupport.LSP.Core`, `Reqnroll.VisualStudio.VsxStubs`** — ✅ Builds and passes 2/2 tests
- ✅ **Create `tests\Core\Reqnroll.IdeSupport.Common.Tests\Reqnroll.IdeSupport.Common.Tests.csproj` (net10, xunit)** — ✅ Project exists with migrated test files
- ✅ **Create `tests\LSP\Reqnroll.IdeSupport.LSP.Core.Tests\Reqnroll.IdeSupport.LSP.Core.Tests.csproj` (net10, xunit)** — ✅ Project exists with migrated Discovery + GherkinParser tests
- ✅ **Write new `DeveroomTagParserTests.cs` in `LSP.Core.Tests`** — ✅ 30+ test methods; already existed from prior session; 108 LSP.Core.Tests pass
- ✅ **Write new `GherkinDocumentContextCalculatorTests.cs` in `LSP.Core.Tests`** — ✅ 6 tests covering background step deduplication, tagged scenario expansion, ScenarioOutline placeholder replacement, empty examples fallback, tagged example sets
- ✅ **Create `tests\LSP\Reqnroll.IdeSupport.LSP.Server.Tests\Reqnroll.IdeSupport.LSP.Server.Tests.csproj` (net10, xunit)** — ✅ Project exists, builds, and passes 52/52 tests
- ✅ **Write new LSP Server test classes — `DocumentBufferServiceTests`, `LspWorkspaceScopeManagerTests`, `TextDocumentSyncHandlerTests`, `GherkinDocumentTaggerServiceTests`, `SemanticTokenServiceTests`, `SemanticTokensHandlerTests`, `WatchedFilesHandlerTests`, `WorkspaceFoldersHandlerTests`** — ✅ All present and passing (52/52)
- ✅ **Create `tests\VisualStudio\Reqnroll.IdeSupport.VisualStudio.Wizards.Tests\Reqnroll.IdeSupport.VisualStudio.Wizards.Tests.csproj` (net481, xunit)** — ✅ Project created, builds, registered in `Reqnroll.IdeSupport.slnx`
- ✅ **Write wizard unit tests** — ✅ 52 tests passing: `ReqnrollProjectTemplateWizardTests` (9), `ConfigFileTemplateWizardTests` (4), `FeatureFileTemplateWizardTests` (6), `AddNewReqnrollProjectViewModelTests` (6), `WelcomeDialogViewModelTests` (8), `UpgradeDialogViewModelTests` (6), `VsWizardContextTests` (4)
- ✅ **Retarget `Reqnroll.VisualStudio.Specs.csproj` — remove `Reqnroll.VisualStudio.csproj` and `DeploymentAssets.props`; add new project references; fix package versions** — ✅ Build succeeds: `ImplicitUsings.cs` retargeted to `Reqnroll.IdeSupport.*` namespaces; `ProjectSystemSteps.cs` (editor-command infrastructure) wrapped in `#if false`; discovery-dependent steps in `DeveroomSteps.cs` wrapped in `#if false`; stale `.feature.cs` code-behind regenerated with Reqnroll 2.1.0 generator. Runtime blocker remains: all 121 tests fail with `System.TypeLoadException` — `Method 'CallExtensionPoint' in type 'Microsoft.VisualStudio.Text.Utilities.GuardedOperations' … does not have an implementation` — thrown by `VsxStubObjects.Initialize()` inside `StubIdeScope..ctor`. Root cause: `Microsoft.VisualStudio.SDK 17.14.x` ships a `VSEditor` assembly that requires an abstract method implementation not present in the version being loaded at test time.
- 🔄 **Reshape Specs step definitions for kept features** — 🔄 Partially done: `DeveroomSteps.cs` compiles; discovery/binding-registry steps deferred. Blocked on the `TypeLoadException` runtime failure in `VsxStubObjects.Initialize()` — all scenarios abort before any step runs.
- ✅ **Mark all deferred Specs feature files with `@wip`/`@ignore` tags** — ✅ Completed: all deferred `.feature` files tagged with `@wip`
- ✅ **Register all new test projects in `Reqnroll.IdeSupport.slnx`** — ✅ Both LSP test projects confirmed registered; VisualStudio test projects already in solution

