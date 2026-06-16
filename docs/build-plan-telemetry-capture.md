# Build Plan: Telemetry Capture & Publication

## Objective

Wire a complete telemetry pipeline that covers both IDE-originated events and
LSP-server events, with a richer event schema that captures discovery source,
trigger context, and incremental results. All events flow through a single
`Microsoft.ApplicationInsights` v2.23.0 `TelemetryClient` in the
`Reqnroll.IdeSupport.Common` project (netstandard2.0).

---

## 1. Prerequisite: Refactor Analytics Infrastructure

Before any events can be wired, the plumbing must exist. This is covered in
detail in the sibling document `plan-refactor-analytics-appinsights.md`; the
high-level steps are summarised here.

### 1.1 Core library (Reqnroll.IdeSupport.Common)

- Add `Microsoft.ApplicationInsights` 2.23.0 NuGet reference
- Rewrite `AnalyticsTransmitter` to own a `TelemetryClient` directly (remove
  `IAnalyticsTransmitterSink` + `ITelemetryConfigurationHolder`)
- `AnalyticsTransmitter` accepts `TelemetryClient`, `IUserUniqueIdStore`,
  `IEnableAnalyticsChecker`, sets `Context.User.Id` and `Context.Properties` at
  init
- Events → `TelemetryClient.TrackEvent(EventTelemetry)` with `IAnalyticsEvent` properties
- Exceptions → `TelemetryClient.TrackException(ExceptionTelemetry)` with additional props
- `IsNormalError` classification preserved
- `ANALYTICS_DEBUG` conditional logging preserved
- `IDisposable` → `FlushAsync()`

### 1.2 VS layer (VSSDKIntegration)

- Remove `AppInsightsAnalyticsTransmitterSink.cs`,
  `ApplicationInsightsConfigurationHolder.cs`,
  `ReqnrollTelemetryContextInitializer.cs`,
  VS-level `IEnableAnalyticsChecker.cs`
- Update VS `AnalyticsTransmitter` MEF export:
  - Read `InstrumentationKey` from embedded resource
  - Create `InMemoryChannel` (`SendingInterval=30s`, `MaxTelemetryBufferCapacity=250`)
  - Create `TelemetryConfiguration { InstrumentationKey, TelemetryChannel }`
  - Create `TelemetryClient(config)`
  - Set `Context.User.Id`, `Context.Properties["Ide"]`,
    `["IdeVersion"]`, `["ExtensionVersion"]`
  - Inject into base `AnalyticsTransmitter`
- Update `MonitoringService.cs` — remove `ITelemetryConfigurationHolder` dependency

### 1.3 LSP client handler (new)

- Add a handler for LSP `telemetry/event` notification in the VS LSP client
  that deserialises the notification params to a `GenericEvent` and forwards to
  `IAnalyticsTransmitter.TransmitEvent()`

---

## 2. Event Schema: Discovery

### 2.1 DiscoverySources

| Source | Description | TriggerContexts |
|--------|-------------|-----------------|
| `Connector` | Out-of-proc reflection discovery (build-based) | `projectLoad`, `build` |
| `Roslyn` | In-process source-level discovery (file-based) | `csOpen`, `csEdit` |

### 2.2 Connector discovery event

Emitted by the LSP server when `ConnectorBindingRegistryProvider.RunDiscoveryAsync`
completes (success or failure). Sent via `telemetry/event`.

```json
{
  "eventName": "Reqnroll Discovery executed",
  "properties": {
    "DiscoverySource": "Connector",
    "ConnectorType": "Generic",
    "TriggerContext": "build",
    "IsFailed": false,
    "ErrorMessage": null,
    "StepDefinitionCount": 42,
    "BindingDelta": null,
    "AffectedFile": null,
    "ProjectCount": null,
    "ReqnrollVersion": "2.2.1",
    "ProjectTargetFramework": "net8.0",
    "SingleFileGeneratorUsed": false,
    "ProgrammingLanguage": "C#",
    "LegacySpecFlow": false
  }
}
```

### 2.3 Roslyn discovery event

Emitted by the LSP server when `CSharpBindingDiscoveryService.UpdateFromSourceAsync`
completes. Sent via `telemetry/event`.

```json
{
  "eventName": "Reqnroll Discovery executed",
  "properties": {
    "DiscoverySource": "Roslyn",
    "ConnectorType": null,
    "TriggerContext": "csEdit",
    "IsFailed": false,
    "ErrorMessage": null,
    "StepDefinitionCount": 44,
    "BindingDelta": "+2",
    "AffectedFile": "MyStepDefs.cs",
    "ProjectCount": 1,
    "ReqnrollVersion": "2.2.1",
    "ProjectTargetFramework": "net8.0",
    "SingleFileGeneratorUsed": false,
    "ProgrammingLanguage": "C#",
    "LegacySpecFlow": false
  }
}
```

### 2.4 Implementation

#### Step 2.4a — Inject telemetry service into LSP server

Add a new service `ILspTelemetryService` to the LSP server project:

```csharp
public interface ILspTelemetryService
{
    void SendEvent(string eventName, Dictionary<string, object?> properties);
}
```

Implementation uses `ILanguageServerFacade.SendNotification("telemetry/event", params)`.
Registered as singleton in `ServiceCollectionExtensions.cs`.

#### Step 2.4b — Wire connector discovery

In `ConnectorBindingRegistryProvider.RunDiscoveryAsync()` (line ~140-170), after
the discovery result is obtained and before/after the `_current` swap, call:

```csharp
_lspTelemetry.SendEvent("Reqnroll Discovery executed", new()
{
    ["DiscoverySource"] = "Connector",
    ["ConnectorType"] = result.ConnectorType ?? _connectorDiscoveryService.GetConnectorType(project),
    ["TriggerContext"] = "build", // or "projectLoad" for first run
    ["IsFailed"] = isFailed,
    ["ErrorMessage"] = errorMessage,
    ["StepDefinitionCount"] = registry?.StepDefinitions.Length ?? 0,
    ["BindingDelta"] = null,
    ["ReqnrollVersion"] = project.ReqnrollVersion,
    ["ProjectTargetFramework"] = project.TargetFrameworkMonikers,
    // ... other project settings
});
```

**Distinguish projectLoad vs build:** The first `RunDiscoveryAsync` after
project scope creation is a project-load trigger. Subsequent runs from
`TriggerRefresh()` are build triggers. Track this with a simple `_isFirstRun`
bool flag on the provider.

#### Step 2.4c — Wire Roslyn discovery

In `CSharpBindingDiscoveryService.UpdateFromSourceAsync()`, after the loop
completes (line ~68), call:

```csharp
_lspTelemetry.SendEvent("Reqnroll Discovery executed", new()
{
    ["DiscoverySource"] = "Roslyn",
    ["ConnectorType"] = null,
    ["TriggerContext"] = isOpen ? "csOpen" : "csEdit",
    ["IsFailed"] = false,
    ["StepDefinitionCount"] = newCount,
    ["BindingDelta"] = deltaStr,
    ["AffectedFile"] = Path.GetFileName(filePath),
    ["ProjectCount"] = owners.Count,
    // ... project settings (from first owner or shared)
});
```

**Distinguish open vs edit:** The handler receives this via the call site
(`DidOpenTextDocumentParams` vs `DidChangeTextDocumentParams`). Pass an
`isOpen` flag through `UpdateFromSourceAsync`, or expose two methods
(`UpdateFromOpenAsync` / `UpdateFromEditAsync`).

#### Step 2.4d — Remove legacy MonitorReqnrollDiscovery

Once the `telemetry/event` path is live, fully delete the `MonitorReqnrollDiscovery`
method from `MonitoringService.cs` and remove its commented-out declaration from
`IMonitoringService.cs`.

---

## 3. Event Schema: Commands (revive from legacy)

### 3.1 Event list

| Event | Properties | Trigger |
|-------|-----------|---------|
| `CommentUncomment command executed` | — | LSP `workspace/executeCommand` `reqnroll.toggleComment` |
| `GoToStepDefinition command executed` | `GenerateSnippet: bool` | LSP `reqnroll.goToStepDefinitions` handler |
| `GoToHook command executed` | — | LSP `reqnroll.goToHooks` handler |
| `FindUnusedStepDefinitions command executed` | `UnusedStepDefinitions: int`, `ScannedFeatureFiles: int`, `IsCancellationRequested: bool` | LSP `reqnroll.findUnusedStepDefinitions` handler |
| `Rename step command executed` | `Erroneous: bool` | VS `RenameStepService` (client-side) |

**Deferred** (commands not yet implemented in current extension):
- `DefineSteps command executed` — not implemented; skip until feature is built
- `FindStepDefinitionUsages command executed` — not implemented; skip
- `AutoFormatTable / AutoFormatDocument command executed` — not implemented; skip

### 3.2 Implementation

For LSP commands, inject `ILspTelemetryService` into each handler and emit the
event at the handler's exit point. This is a one-liner per handler.

Example (`GoToStepDefinitionsHandler`):
```csharp
public async Task<GoToStepDefinitionsResponse> HandleAsync(...)
{
    var result = await ...;
    _lspTelemetry.SendEvent("GoToStepDefinition command executed", new()
    {
        ["GenerateSnippet"] = result.MatchType == MatchResultType.Undefined
    });
    return result;
}
```

For the VS client-side `RenameStepService`, inject `IAnalyticsTransmitter`
directly (it's in the IDE process, no LSP round-trip needed).

---

## 4. Event Schema: New Events

### 4.1 Completion inserted

Emitted when a user accepts a completion item from the LSP completion handler.

| Property | Example |
|----------|---------|
| `InsertedTextLength` | 12 |
| `Context` | `"stepDefinition"` \| `"keyword"` \| `"snippet"` |
| `FileType` | `"feature"` |

**Location:** After completion is resolved and applied in the LSP completion
handler. Requires `ILspTelemetryService`.

### 4.2 Connector hash-noop rate

Emitted when `ConnectorDiscoveryService.RunDiscovery` returns the current hash
unchanged (no new bindings). This is a lightweight signal to understand build
churn.

| Property | Example |
|----------|---------|
| `HashMatched` | true |
| `ElapsedMs` | 1234 |

**Location:** In `ConnectorBindingRegistryProvider.RunDiscoveryAsync()` where
the hash comparison happens (line ~155). If hash matches, emit a lightweight
`hashMatch` event; otherwise the full discovery event is sufficient.

### 4.3 Error recovery / LSP crash

Emitted when the LSP server auto-restarts or a connector failure is handled.

| Property | Example |
|----------|---------|
| `ErrorType` | `"connectorTimeout"` \| `"serverCrash"` |
| `RecoveryAction` | `"restarted"` \| `"retried"` |

**Location:** Error-handling boundaries in `ConnectorDiscoveryService` and
the LSP server startup.

---

## 5. Build Order

### Phase A — Infrastructure
1. Add `Microsoft.ApplicationInsights` 2.23.0 to Common.csproj
2. Rewrite `AnalyticsTransmitter` (remove sink pattern, own TelemetryClient)
3. Create VS-level `AnalyticsTransmitter` MEF export (config, channel, client)
4. Remove deprecated VS files (sink, config holder, context initializer)
5. Update `MonitoringService` — remove `ITelemetryConfigurationHolder`
6. Add `telemetry/event` handler in VS LSP client
7. Create `ILspTelemetryService` in LSP server
8. Register `ILspTelemetryService` in DI

### Phase B — Discovery events
9. Wire connector discovery event in `ConnectorBindingRegistryProvider`
10. Wire Roslyn discovery event in `CSharpBindingDiscoveryService`
11. Remove dead `MonitorReqnrollDiscovery` code

### Phase C — Command events
12. Wire `GoToStepDefinition command executed` in `GoToStepDefinitionsHandler`
13. Wire `GoToHook command executed` in `GoToHooksHandler`
14. Wire `FindUnusedStepDefinitions command executed` in handler
15. Wire `CommentUncomment command executed` in `CommentToggleHandler`
16. Wire `Rename step command executed` in `RenameStepService`

### Phase D — New events
17. Wire `Completion inserted` in completion handler
18. Wire connector hash-noop event
19. Wire error recovery events

### Phase E — Cleanup
20. Remove all commented-out monitor methods and `GenericEvent` emit lines
21. Update `IMonitoringService` interface — remove methods that are now
    `telemetry/event` only (keep IDE-only ones like wizard, dialog, error)
22. Verify `REQNROLL_TELEMETRY_ENABLED=0` suppresses all event paths
23. Update tests

---

## 6. Testing Plan

### 6.1 Unit tests (AnalyticsTransmitter in Common.Tests)

| Test | Covers |
|------|--------|
| `Should_ForwardEventToTelemetryClient` | `TrackEvent` called when enabled |
| `Should_SuppressEventWhenDisabled` | No `TrackEvent` when checker returns false |
| `Should_ForwardExceptionWithIsFatal` | `TrackException` with `IsFatal` property |
| `Should_FlushOnDispose` | `FlushAsync` called on `Dispose()` |
| `Should_SetUserIdAndContextProperties` | `Context.User.Id` and `Context.Properties` populated |
| `Should_NotThrowOnTransmissionFailure` | Catch-all safety |

### 6.2 LSP telemetry/event tests

- Unit test for `ILspTelemetryService` that verifies `SendNotification` is called
- Unit test for LSP client handler that verifies `IAnalyticsTransmitter.TransmitEvent`
  is called with deserialised event

### 6.3 Integration / manual tests

| Test | How |
|------|-----|
| Connector discovery event fires | Trigger build, verify event in AppInsights portal |
| Roslyn discovery event fires | Open/edit .cs file, verify event arrives |
| Command events fire | Invoke GoToStepDefinition, verify event |
| Opt-out suppresses all | Set env var, verify no events |
| Shutdown flushes | Close VS, verify no lost events |
| Hash-noop not noisy | Consecutive builds with no code changes produce only hashMatch events |

---

## 7. Risk Assessment

| Risk | Impact | Mitigation |
|------|--------|------------|
| LSP `telemetry/event` not supported by the OmniSharp LSP library | Medium | Check OmniSharp docs; if unsupported, use a custom notification method |
| `telemetry/event` handler not yet implemented in LSP client | Medium | Straightforward to add; just deserialise and forward |
| Roslyn discovery fires on every keystroke (too many events) | Medium | Debounce in `CSharpBindingDiscoveryService` (the discovery is already debounced by the 500ms trigger in `ConnectorBindingRegistryProvider`; Roslyn discovery currently fires on every keystroke — may need a throttle or coalesce at the telemetry layer) |
| Command handlers not yet instrumentable (no DI in some handlers) | Low | All LSP handlers go through DI; `ILspTelemetryService` is injectable |
| Project settings not available in Roslyn discovery path | Low | The `CSharpBindingDiscoveryService` already resolves project owners via `ILspWorkspaceScopeManager` — project settings can be read from the scope |
