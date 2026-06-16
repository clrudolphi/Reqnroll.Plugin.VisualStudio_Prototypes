# Plan: Refactor Telemetry Subsystem

## Objective

Replace the current hand-rolled Application Insights client (using the VS SDK's
`Microsoft.VisualStudio.ApplicationInsights` namespace) with the standard
`Microsoft.ApplicationInsights` NuGet package (v3.x). The entire subsystem lives
in `Reqnroll.IdeSupport.Common` (netstandard2.0) so it can be consumed by the
Visual Studio extension, future VS Code extensions, and future Rider extensions
alike.

Architecture principle: **the IDE client owns the analytics dependency**. The LSP
server has no reference to `Microsoft.ApplicationInsights`. Instead, server-side
events flow to the client via the LSP `telemetry/event` notification, where they
are merged with IDE-originated events into a single `TelemetryClient` instance.

**Incompatibility note:** `Microsoft.ApplicationInsights.WorkerService` (v3.1.2)
requires `net8.0+` and is not usable. The plan uses the base
`Microsoft.ApplicationInsights` package (v3.1.2), which targets `netstandard2.0`
(verified: its full dependency graph resolves for netstandard2.0).

---

## 1. Data Flow (Target Architecture)

```
┌─────────────────────────────────────────────────────────────────────────┐
│ LSP Server (out-of-proc)                                                │
│  No AppInsights reference.                                              │
│  Fires telemetry as LSP notifications.                                  │
│                                                                         │
│  ┌──────────────┐     LSP wire     ┌────────────────────┐              │
│  │ Discovery    │   telemetry/     │ IDictionary<str,…> │              │
│  │ Parser       │   event notify   │ → event name       │              │
│  │ Connector    │  ─────────────→  │ → properties       │              │
│  │ Generation   │                  └────────────────────┘              │
│  └──────────────┘                                                      │
└─────────────────────────────────────────────────────────────────────────┘
         ↕ (stdin/stdout — already connected)
┌─────────────────────────────────────────────────────────────────────────┐
│ IDE Client Process (VS / VS Code / Rider)                               │
│  Owns the analytics subsystem. Single TelemetryClient per process.     │
│                                                                         │
│  ┌──────────────────────────────┐                                       │
│  │ telemetry/event handler       │                                       │
│  │  → deserialize to IAnalytics…│                                       │
│  │  → add IDE context properties│                                       │
│  │  → AnalyticsTransmitter       │                                       │
│  └──────────┬───────────────────┘                                       │
│             │                                                           │
│  ┌──────────▼───────────────────┐   ┌───────────────────────────────┐  │
│  │ MonitoringService (IDE-scope)│   │ NullMonitoringService         │  │
│  │  → MonitorOpenProject        │   │ (no-op for headless/testing)  │  │
│  │  → MonitorExtensionInstalled │   └───────────────────────────────┘  │
│  │  → MonitorError              │                                       │
│  │  → MonitorWizard...          │                                       │
│  └──────────┬───────────────────┘                                       │
│             │                                                           │
│  ┌──────────▼──────────────────────────────────────────────────────┐   │
│  │ AnalyticsTransmitter  (in Reqnroll.IdeSupport.Common)           │   │
│  │  ┌──────────────────────────────────────────────────────────┐  │   │
│  │  │ Microsoft.ApplicationInsights.TelemetryClient             │  │   │
│  │  │  → TrackEvent / TrackException / Flush                    │  │   │
│  │  │  → Context.User.Id = from IUserUniqueIdStore              │  │   │
│  │  │  → Context.GlobalProperties = IDE name, version, etc.     │  │   │
│  │  └──────────────────────────────────────────────────────────┘  │   │
│  └─────────────────────────────────────────────────────────────────┘   │
│                                                                         │
│  ┌──────────────────────────────────────┐                               │
│  │ IEnableAnalyticsChecker               │                               │
│  │  → Environment variable opt-out       │                               │
│  │    REQNROLL_TELEMETRY_ENABLED=0       │                               │
│  │  → Gates all events (LSP + IDE)       │                               │
│  └──────────────────────────────────────┘                               │
└─────────────────────────────────────────────────────────────────────────┘
```

Two telemetry sources converge into a single pipeline:

| Source | How it arrives | Examples |
|--------|---------------|---------|
| **LSP server** | `telemetry/event` notification → deserialized by LSP client handler → forwarded to `AnalyticsTransmitter` | discovery result, parser event, generation result |
| **IDE code** | Direct call from `MonitoringService` → `AnalyticsTransmitter` | extension loaded/installed/upgraded, project opened, feature file opened, errors, wizard events |

---

## 2. Current Architecture (Inventory)

### 2.1 Files in `src/Core/Reqnroll.IdeSupport.Common/Analytics/`

| File | Purpose | Fate |
|------|---------|------|
| `IAnalyticsEvent.cs` | Event contract: EventName + Properties | **Stays** |
| `IAnalyticsTransmitter.cs` | Send event / exception / fatal-exception | **Stays** |
| `IAnalyticsTransmitterSink.cs` | Transport abstraction (current sink pattern) | **Removed** — replaced by direct TelemetryClient |
| `AnalyticsTransmitter.cs` | Gateway: checks enable, delegates to sink | **Rewritten** — owns TelemetryClient directly |
| `GenericEvent.cs` | Simple named event record | **Stays** |
| `DiscoveryResultEvent.cs` | Commented-out LSP result event | **Stays** (or removed during cleanup) |
| `IEnableAnalyticsChecker.cs` | Opt-out via env var | **Stays** |
| `EnableAnalyticsChecker` (inline) | Opt-out implementation | **Stays** |
| `IUserUniqueIdStore.cs` | User identity abstraction | **Stays** (TelemetryClient reads via this) |
| `IRegistryManager.cs` | Registry read/write for install status | **Stays** |
| `ReqnrollInstallationStatus.cs` | Install/usage tracking model | **Stays** |
| `GuidanceConfiguration.cs` | Usage milestone definitions | **Stays** |
| `GuidanceStep.cs` | Step model for guidance notifications | **Stays** |
| `GuidanceNotification.cs` | Enum for notification levels | **Stays** |
| `InstrumentationKey.txt` | Embedded resource with instrumentation key GUID | **Replaced** — v3.x needs ConnectionString |

### 2.2 Files in `src/VisualStudio/.../Analytics/`

| File | Purpose | Fate |
|------|---------|------|
| `AppInsightsAnalyticsTransmitterSink.cs` | Sink using VS SDK TelemetryClient | **Removed** — replaced by Core's AnalyticsTransmitter |
| `ApplicationInsightsConfigurationHolder.cs` | Reads key from embedded resource, sets `TelemetryConfiguration.Active` (global static) | **Removed** — no global static in v3.x; config done at client creation |
| `ReqnrollTelemetryContextInitializer.cs` | Adds IDE/version/user props via IContextInitializer | **Removed** — replaced by TelemetryClient.Context.GlobalProperties |
| `AnalyticsTransmitter.cs` | Thin MEF export wrapper over Core | **Simplified** — just creates TelemetryClient, wires deps, inherits Core |
| `IEnableAnalyticsChecker.cs` | Thin MEF export wrapper | **Removed** — EnableAnalyticsChecker is already in Core, just export that |
| `FileUserIdStore.cs` | Persists userId to `%APPDATA%\Reqnroll\userid` | **Stays** — implements IUserUniqueIdStore |

### 2.3 Related files

| File | Fate |
|------|------|
| `Monitor/MonitoringService.cs` | **Minimal change** — remove `ITelemetryConfigurationHolder` dependency |
| `ITelemetryConfigurationHolder.cs` (Core) | **Removed** — no longer needed |
| `NullMonitoringService.cs` (LSP Server) | **Stays** — LSP server uses no-op; events go via telemetry/event instead |
| `VsWizardTelemetry.cs` (Wizards) | **Stays** — depends on IMonitoringService only |
| `StubAnalyticsTransmitter.cs` (tests) | **Stays** — test double for IAnalyticsTransmitter |

### 2.4 LSP Server files (new telemetry/event handler needed)

| File | Fate |
|------|------|
| `LSP/.../Workspace/NullMonitoringService.cs` | **Stays** — LSP server doesn't use IMonitoringService for analytics |
| (none yet) — need to add `telemetry/event` emission in server | **New** — wire server events as LSP telemetry notifications |

---

## 3. Build Inventory (Net Changes)

### 3.1 NuGet package

| Project | Package | Version | Action |
|---------|---------|---------|--------|
| `Reqnroll.IdeSupport.Common.csproj` (netstandard2.0) | `Microsoft.ApplicationInsights` | 3.1.2 | **Add** |
| `Reqnroll.IdeSupport.VisualStudio.VSSDKIntegration.csproj` (net481) | (transitive only via Common) | — | **None** |

### 3.2 Files to Remove

| # | File | Reason |
|---|------|--------|
| 1 | `src/VS/.../Analytics/AppInsightsAnalyticsTransmitterSink.cs` | Sink pattern eliminated; TelemetryClient lives in Core |
| 2 | `src/VS/.../Analytics/ApplicationInsightsConfigurationHolder.cs` | Global TelemetryConfiguration.Active removed in v3.x |
| 3 | `src/VS/.../Analytics/ReqnrollTelemetryContextInitializer.cs` | ContextInitializer pattern removed in v3.x; use GlobalProperties |
| 4 | `src/VS/.../Analytics/IEnableAnalyticsChecker.cs` | Thin MEF export — EnableAnalyticsChecker already exists in Core and can be MEF-exported from there |
| 5 | `src/Core/.../Analytics/IAnalyticsTransmitterSink.cs` | Sink abstraction no longer needed |
| 6 | `src/Core/.../ITelemetryConfigurationHolder.cs` | Configuration holder pattern eliminated |
| 7 | `src/Core/.../Analytics/InstrumentationKey.txt` | Replaced by connection string (or updated in place to connection string format) |

### 3.3 Files to Create or Modify

| Action | File | What changes |
|--------|------|-------------|
| **Modify** | `Core/Analytics/AnalyticsTransmitter.cs` | Accept `TelemetryClient` + `IUserUniqueIdStore` + `IEnableAnalyticsChecker`. Route events to `TelemetryClient.TrackEvent/TrackException`. Set `Context.User.Id` and `Context.GlobalProperties` at init. Implement `IDisposable` → `Flush()`. Preserve `IsNormalError` classification. Preserve `ANALYTICS_DEBUG` conditional logging. |
| **Modify** | `Core/Reqnroll.IdeSupport.Common.csproj` | Add `<PackageReference Include="Microsoft.ApplicationInsights" Version="3.1.2" />` |
| **Modify** | `VS/Analytics/AnalyticsTransmitter.cs` (MEF) | Instead of `: base(sink, checker, logger)`, create `TelemetryClient(config)`, set `Context.User.Id` and `Context.GlobalProperties` from `IUserUniqueIdStore`/`IVersionProvider`, inject into base. Read connection string from embedded resource. |
| **Modify** | `Common/ITelemetryConfigurationHolder.cs` → **delete** | Remove interface + remove import from MonitoringService.cs |
| **Modify** | `VS/Monitoring/MonitoringService.cs` | Remove `ITelemetryConfigurationHolder telemetryConfigurationHolder` parameter |
| **New** | `LSP/.../Workspace/LspTelemetryService.cs` (or similar) | In the LSP server, a service that fires `telemetry/event` notifications for discovery, parser, and generation events. The LSP client handler deserializes these and forwards to `AnalyticsTransmitter`. |

### 3.4 Connection string configuration

The current `InstrumentationKey.txt` resource contains a bare GUID
(`3fd018ff-819d-4685-a6e1-6f09bc98d20b`). In v3.x, `InstrumentationKey` is
removed in favour of `ConnectionString`.

**Approach:** Keep the embedded resource approach but store the full connection
string. The VS-level `AnalyticsTransmitter` reads it at construction time:

```csharp
// In VS project's AnalyticsTransmitter constructor:
using var stream = assembly.GetManifestResourceStream("...ConnectionString.txt");
using var reader = new StreamReader(stream);
var connectionString = reader.ReadLine() ?? throw InvalidOperationException("Missing connection string");
var config = TelemetryConfiguration.CreateDefault();
config.ConnectionString = connectionString;
var client = new TelemetryClient(config);
```

Or continue to store just the key and construct the connection string
programmatically — the latter is more flexible (avoids hardcoding the ingestion
endpoint).

---

## 4. Implementation Phases

### Phase 1 — Core library changes

1. Add `Microsoft.ApplicationInsights` 3.1.2 NuGet reference to `Reqnroll.IdeSupport.Common.csproj`
2. Rewrite `AnalyticsTransmitter` to:
   - Accept `TelemetryClient` + `IUserUniqueIdStore` + `IEnableAnalyticsChecker` (remove `IAnalyticsTransmitterSink`)
   - On first event: validate enabled, set `Context.User.Id` and `Context.GlobalProperties`
   - `TransmitEvent` → `_telemetryClient.TrackEvent(eventTelemetry)` with `IAnalyticsEvent` properties mapped
   - `TransmitExceptionEvent` / `TransmitFatalExceptionEvent` → `_telemetryClient.TrackException(exceptionTelemetry)` with additional props
   - Preserve `IsNormalError` classification (normal errors → TrackException; fatal → TrackException with IsFatal property)
   - Preserve `ANALYTICS_DEBUG` conditional logging
   - Implement `IDisposable` / `IAsyncDisposable` → `Flush()`
3. Remove `IAnalyticsTransmitterSink.cs`
4. Remove `ITelemetryConfigurationHolder.cs`
5. Remove `InstrumentationKey.txt` embedded resource (or update it)
6. Update .csproj: remove the embedded resource entry (or point to new connection string resource)

### Phase 2 — VS layer cleanup

1. Remove `AppInsightsAnalyticsTransmitterSink.cs`
2. Remove `ApplicationInsightsConfigurationHolder.cs`
3. Remove `ReqnrollTelemetryContextInitializer.cs`
4. Remove VS-level `IEnableAnalyticsChecker.cs` (MEF export from Core directly instead)
5. Update VS `AnalyticsTransmitter.cs` (MEF export) to:
   - Read connection string from embedded resource (or construct from key)
   - Create `TelemetryConfiguration.CreateDefault()` + set `ConnectionString`
   - Create `TelemetryClient(config)`
   - Set `Context.User.Id = userStore.GetUserId()`
   - Set `Context.GlobalProperties["Ide"] = "Microsoft Visual Studio"`
   - Set `Context.GlobalProperties["IdeVersion"] = versionProvider.GetVsVersion()`
   - Set `Context.GlobalProperties["ExtensionVersion"] = versionProvider.GetExtensionVersion()`
   - Pass `TelemetryClient` + `IUserUniqueIdStore` + `IEnableAnalyticsChecker` to base
6. Update `MonitoringService.cs` — remove `ITelemetryConfigurationHolder` parameter

### Phase 3 — LSP telemetry/event wiring

1. **LSP Server side:** Add a service that emits `telemetry/event` notifications.
   The notification body is a `Dictionary<string, object?>` with at minimum an
   `"eventName"` key and `"properties"` key. Example notification params:

   ```
   { "eventName": "Reqnroll Discovery executed",
     "properties": { "IsFailed": false, "StepDefinitionCount": 12,
                     "ConnectorType": "Process" } }
   ```

2. **LSP Client side:** Add a handler for `telemetry/event` in the VS LSP client
   that:
   - Receives the notification params dict
   - Extracts `eventName` and `properties`
   - Constructs a `GenericEvent(eventName, properties)`
   - Calls `_analyticsTransmitter.TransmitEvent(...)`

   (Future VS Code and Rider LSP clients will add equivalent handlers.)

3. **Server events to wire** (currently hard-skipped in `NullMonitoringService`):
   - Discovery result
   - Parser events
   - Generation events

### Phase 4 — Testing

1. **Rewrite `AnalyticsTransmitterTests`** — swap test double from
   `IAnalyticsTransmitterSink` to `TelemetryClient`. Assert `TrackEvent` /
   `TrackException` were called (or not) on the client. Verify `Context.User.Id`
   and `Context.GlobalProperties` are populated.

2. **New unit tests:**
   - Verify enable/disable gate still works
   - Verify `IsNormalError` classification (normal vs fatal exceptions)
   - Verify `Flush()` on `Dispose()`
   - Verify `ANALYTICS_DEBUG` conditional logging
   - Verify no exception thrown when AppInsights transmission fails (catch-all)

3. **Check existing stubs compile:**
   - `StubAnalyticsTransmitter` (implements `IAnalyticsTransmitter`) — unchanged
   - `NullMonitoringService` — unchanged

### Phase 5 — Connection string / cleanup

1. Decide on connection string format in embedded resource
2. Ensure `TelemetryClient` disposal is wired to VS package shutdown
3. Remove unused `using` statements throughout
4. Verify `REQNROLL_TELEMETRY_ENABLED=0` suppresses all telemetry (LSP + IDE)
5. Verify no exceptions escape the telemetry path

---

## 5. Testing Plan

### 5.1 Existing tests — unchanged

| Test file | Scope |
|-----------|-------|
| `tests/.../Common.Tests/Analytics/AnalyticsTransmitterTests.cs` | Refactored to test against `TelemetryClient` substitute instead of `IAnalyticsTransmitterSink` — same test cases, same assertions shape |

### 5.2 Existing test doubles — unchanged

| File | Why it compiles |
|------|----------------|
| `tests/.../VsxStubs/StubAnalyticsTransmitter.cs` | Implements `IAnalyticsTransmitter`, not `IAnalyticsTransmitterSink` |
| `LSP/.../NullMonitoringService.cs` | Implements `IMonitoringService` |

### 5.3 New / updated unit tests in Common.Tests

| Test | What it covers |
|------|---------------|
| `Should_NotSendAnalytics_WhenDisabled` | Verifies no TrackEvent/TrackException calls when checker returns false |
| `Should_SendAnalytics_WhenEnabled` | Verifies TrackEvent/TrackException calls when checker returns true |
| `Should_SetUserId_OnContext` | Verifies `TelemetryClient.Context.User.Id` is set from `IUserUniqueIdStore` |
| `Should_SetGlobalProperties_OnContext` | Verifies IDE metadata appears in `GlobalProperties` |
| `Should_ClassifyNormalErrors` | `TimeoutException`, `TaskCanceledException`, `HttpRequestException` → still tracked |
| `Should_ClassifyFatalExceptions` | Unknown exception types → tracked with `IsFatal` property |
| `Should_FlushOnDispose` | `Dispose()` → calls `TelemetryClient.Flush()` |
| `Should_NotThrow_WhenTransmissionFails` | Catch-all safety net (existing behaviour) |
| `Should_LogInDebugMode` | `ANALYTICS_DEBUG` conditional logging (existing behaviour) |

### 5.4 Manual / integration tests

| Test | How |
|------|-----|
| Events appear in App Insights | Launch VS extension, trigger scenarios, verify in Azure portal |
| Opt-out works | Set `REQNROLL_TELEMETRY_ENABLED=0`, launch, verify no events |
| LSP telemetry arrives | Trigger discovery, verify event appears from telemetry/event handler |
| Safe on failure | Mock transmission failure, verify no crash |
| Shutdown flushes | Close VS, verify no lost events in final flush window |

---

## 6. Key Design Decisions

| Decision | Choice | Rationale |
|----------|--------|-----------|
| Where does `TelemetryClient` live? | `Reqnroll.IdeSupport.Common` (netstandard2.0) | Shared across all IDE extensions; VS Code / Rider can reuse without duplication |
| Who owns `Microsoft.ApplicationInsights` dependency? | Only `Reqnroll.IdeSupport.Common.csproj` | Single point of truth; VS/VSSDKIntegration gets it transitively |
| How do LSP events reach AppInsights? | `telemetry/event` LSP notification → client deserializes → forwards to `AnalyticsTransmitter` | Protocol-conformant; server stays clean; single pipeline with IDE events |
| How to deal with v3.x missing `TelemetryConfiguration.Active`? | Each IDE creates its own `TelemetryClient(config)` at startup | Instance-based config; no global state; clear lifecycle |
| `WorkerService` package? | Not used | Requires net8.0+; incompatible with both netstandard2.0 and net481 |
| Flush strategy | `Dispose()` calls `Flush()`; rely on SDK auto-flush during runtime | Manual flush-after-each-event was wasteful; SDK auto-flush is adequate for a VS extension |
| Connection string storage | Embedded resource in VS project | Same pattern as current `InstrumentationKey.txt`; stores `InstrumentationKey=...;IngestionEndpoint=...` |

---

## 7. Risk Assessment

| Risk | Impact | Mitigation |
|------|--------|------------|
| v3.x API differs substantially from VS SDK AppInsights | Medium | Migration is well-documented; `TelemetryClient.TrackEvent/TrackException` API shape preserved |
| `TelemetryConfiguration.Active` removed | Medium | Use `CreateDefault()` + instance-based config |
| `IContextInitializer` removed | Low | Use `Context.GlobalProperties` — functionally equivalent |
| Connection string required in v3.x (throws if missing) | Medium | Update embedded resource with valid connection string; fail fast at startup |
| `TelemetryClient` disposal vs singleton lifetime | Low | Dispose on VS package close; SDK auto-flush handles runtime |
| LSP `telemetry/event` handler not yet implemented | Low | Straightforward: receive `IDictionary<string, object?>`, construct `GenericEvent`, forward to `IAnalyticsTransmitter` |
| OpenTelemetry transitive deps increase Common project's dependency footprint | Low | Dependencies are runtime-only for netstandard2.0; no DI/Hosting features are exercised |

---

## 8. Open Questions

1. **Connection string format in embedded resource** — should the resource store the full connection string (`InstrumentationKey=3fd018ff-819d-4685-a6e1-6f09bc98d20b;IngestionEndpoint=...`) or should the code construct it from the key GUID? Full connection string is more robust (no hardcoded endpoint); key-only is simpler migration.

2. **`TelemetryClient` as a dependency** — injected via constructor (testable) or created internally in `AnalyticsTransmitter`? Constructor injection is cleaner for testing (pass a substitute). The VS project's MEF export creates the real client and injects it.

3. **LSP `telemetry/event` schema** — what keys go in the notification params dictionary? Minimum: `eventName` (string) + `properties` (dictionary). Can the properties contain non-string values? (Current `IAnalyticsEvent.Properties` is `ImmutableDictionary<string, object>` so yes — but the handler should serialise safely.)

4. **Should the server send telemetry before client connection?** For discovery/parser events, this is never an issue (they happen on request, after client is connected). A guard in the server's telemetry service ("skip if no client") is easy to add.

5. **Verify `NullMonitoringService` usage** — the LSP server uses `NullMonitoringService` which currently no-ops `IMonitoringService` calls. With the telemetry/event approach, this stays correct — neither `IMonitoringService` nor `IAnalyticsTransmitter` are needed in the server process.
