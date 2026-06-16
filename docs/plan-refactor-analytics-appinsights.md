# Plan: Refactor Telemetry Subsystem

## Objective

Replace the current hand-rolled Application Insights client (using the VS SDK's
`Microsoft.VisualStudio.ApplicationInsights` namespace) with the standard
`Microsoft.ApplicationInsights` NuGet package **v2.23.0**. The entire subsystem
lives in `Reqnroll.IdeSupport.Common` (netstandard2.0) so it can be consumed by
the Visual Studio extension, future VS Code extensions, and future Rider
extensions alike.

Architecture principle: **the IDE client owns the analytics dependency**. The LSP
server has no reference to `Microsoft.ApplicationInsights`. Instead, server-side
events flow to the client via the LSP `telemetry/event` notification, where they
are merged with IDE-originated events into a single `TelemetryClient` instance.

### Why 2.23.0 instead of 3.1.2

| Factor | 3.1.2 | 2.23.0 |
|--------|-------|--------|
| Dependency tree | 5+ packages (OpenTelemetry stack) | **1** (`System.Diagnostics.DiagnosticSource`) |
| `TelemetryConfiguration.Active` | Removed | **Available** |
| `InstrumentationKey` | Removed (requires ConnectionString) | **Available** — bare GUID works |
| `IContextInitializer` / `ITelemetryInitializer` | Removed (use `GlobalProperties`) | **Available** (or set `Context.Properties` directly) |
| `FlushAndTransmitAsync` | Removed (auto-flush) | `Flush()` / `FlushAsync()` |
| .NET Framework fidelity | OpenTelemetry stack unproven on net481 | **146M+ downloads**, proven |
| API alignment with current code | Substantially different | **Near-identical** — mostly a namespace swap |

`Microsoft.ApplicationInsights.WorkerService` (both 2.x and 3.x) is not used.
It targets netstandard2.0 but pulls in 7+ collector/logging/DependencyInjection
packages we don't need — we wire `TelemetryClient` manually.

The base `Microsoft.ApplicationInsights` 2.23.0 gives us everything we need.

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
│  │  │ Microsoft.ApplicationInsights.TelemetryClient  (2.23.0)  │  │   │
│  │  │  → TrackEvent / TrackException / FlushAsync              │  │   │
│  │  │  → Context.User.Id = from IUserUniqueIdStore             │  │   │
│  │  │  → Context.Properties["Ide"] = "Microsoft Visual Studio" │  │   │
│  │  │  → Context.Properties["IdeVersion"] = …                  │  │   │
│  │  │  → Context.Properties["ExtensionVersion"] = …            │  │   │
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
| `InstrumentationKey.txt` | Embedded resource with instrumentation key GUID | **Stays** — 2.x supports bare GUID; key format unchanged |

### 2.2 Files in `src/VisualStudio/.../Analytics/`

| File | Purpose | Fate |
|------|---------|------|
| `AppInsightsAnalyticsTransmitterSink.cs` | Sink using VS SDK TelemetryClient | **Removed** — replaced by Core's AnalyticsTransmitter |
| `ApplicationInsightsConfigurationHolder.cs` | Reads key from embedded resource, sets `TelemetryConfiguration.Active` | **Removed** — config moves to VS-level AnalyticsTransmitter |
| `ReqnrollTelemetryContextInitializer.cs` | Adds IDE/version/user props via `IContextInitializer` | **Removed** — same logic set directly on `TelemetryClient.Context.Properties` |
| `AnalyticsTransmitter.cs` | Thin MEF export wrapper over Core | **Simplified** — creates TelemetryClient, wires deps, inherits Core |
| `IEnableAnalyticsChecker.cs` | Thin MEF export wrapper | **Removed** — EnableAnalyticsChecker is already in Core, MEF-export from there |
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
| `Reqnroll.IdeSupport.Common.csproj` (netstandard2.0) | `Microsoft.ApplicationInsights` | **2.23.0** | **Add** |
| `Reqnroll.IdeSupport.VisualStudio.VSSDKIntegration.csproj` (net481) | (transitive only via Common) | — | **None** |

**Dependency footprint:** `Microsoft.ApplicationInsights` 2.23.0 depends on
`System.Diagnostics.DiagnosticSource` ≥ 5.0.0 — and nothing else.

### 3.2 Files to Remove

| # | File | Reason |
|---|------|--------|
| 1 | `src/VS/.../Analytics/AppInsightsAnalyticsTransmitterSink.cs` | Sink pattern eliminated; TelemetryClient lives in Core |
| 2 | `src/VS/.../Analytics/ApplicationInsightsConfigurationHolder.cs` | Config moves to the VS-level AnalyticsTransmitter MEF export |
| 3 | `src/VS/.../Analytics/ReqnrollTelemetryContextInitializer.cs` | Properties set directly on TelemetryClient.Context at creation |
| 4 | `src/VS/.../Analytics/IEnableAnalyticsChecker.cs` | Thin MEF export — Core's EnableAnalyticsChecker already exists |
| 5 | `src/Core/.../Analytics/IAnalyticsTransmitterSink.cs` | Sink abstraction no longer needed |
| 6 | `src/Core/.../ITelemetryConfigurationHolder.cs` | Configuration holder pattern eliminated |

### 3.3 Files to Create or Modify

| Action | File | What changes |
|--------|------|-------------|
| **Modify** | `Core/Analytics/AnalyticsTransmitter.cs` | Accept `TelemetryClient` + `IUserUniqueIdStore` + `IEnableAnalyticsChecker`. Route events to `TelemetryClient.TrackEvent`/`TrackException`. Set `Context.User.Id` and `Context.Properties` at init (from IUserUniqueIdStore + config). Implement `IDisposable` → `Flush()`. Preserve `IsNormalError` classification. Preserve `ANALYTICS_DEBUG` conditional logging. |
| **Modify** | `Core/Reqnroll.IdeSupport.Common.csproj` | Add `<PackageReference Include="Microsoft.ApplicationInsights" Version="2.23.0" />` |
| **Modify** | `VS/Analytics/AnalyticsTransmitter.cs` (MEF) | Create `TelemetryConfiguration` instance, set `InstrumentationKey` (read from embedded resource), create `TelemetryClient(config)`, set `Context.User.Id` and `Context.Properties` for IDE metadata, inject into base `AnalyticsTransmitter` |
| **Modify** | `VS/Monitoring/MonitoringService.cs` | Remove `ITelemetryConfigurationHolder telemetryConfigurationHolder` parameter |
| **New** | `LSP/.../Workspace/LspTelemetryService.cs` (or similar) | In LSP server, emit `telemetry/event` notifications for discovery, parser, generation events |
| **New** | `.../LspClient/TelemetryEventHandler.cs` (or similar) | In LSP client, handle `telemetry/event`, deserialize to `GenericEvent`, forward to `IAnalyticsTransmitter` |

### 3.4 Configuration approach (2.x) — channel and flush strategy

The channel is **InMemoryChannel** with sensible defaults tuned for a VS
extension. No `ServerTelemetryChannel` (disk-backed) is needed — telemetry loss
on crash is acceptable and matches current behaviour.

**Key change from today:** the current code calls `FlushAndTransmitAsync()`
after **every single event** with a 1-second timeout, defeating batching. The
new approach lets the channel batch naturally and only flushes explicitly on
shutdown.

```csharp
// In VS project's AnalyticsTransmitter MEF export (constructor):
var channel = new InMemoryChannel
{
    SendingInterval = TimeSpan.FromSeconds(30),
    MaxTelemetryBufferCapacity = 250
};

var config = new TelemetryConfiguration
{
    InstrumentationKey = keyFromEmbeddedResource,
    TelemetryChannel = channel
};

var client = new TelemetryClient(config);
client.Context.User.Id = userStore.GetUserId();
client.Context.Properties["Ide"] = "Microsoft Visual Studio";
client.Context.Properties["IdeVersion"] = versionProvider.GetVsVersion();
client.Context.Properties["ExtensionVersion"] = versionProvider.GetExtensionVersion();

// Pass client into base:
var transmitter = new AnalyticsTransmitter(client, userStore, enableChecker, logger);
```

No global `TelemetryConfiguration.Active`. No `IContextInitializer`. Each IDE
creates its own `TelemetryClient` with its own config and context properties.

---

## 4. Implementation Phases

### Phase 1 — Core library changes

1. Add `Microsoft.ApplicationInsights` **2.23.0** NuGet reference to
   `Reqnroll.IdeSupport.Common.csproj`
2. Rewrite `AnalyticsTransmitter` to accept `TelemetryClient` + `IUserUniqueIdStore` +
   `IEnableAnalyticsChecker` (remove `IAnalyticsTransmitterSink`)
3. Route events:
   - `TransmitEvent(IAnalyticsEvent)` → `_telemetryClient.TrackEvent(EventTelemetry)` with
     `IAnalyticsEvent.Properties` mapped via `ISupportProperties`
   - `TransmitExceptionEvent` / `TransmitFatalExceptionEvent` →
     `_telemetryClient.TrackException(ExceptionTelemetry)` with additional props
4. Preserve `IsNormalError` classification (normal → TrackException; fatal → TrackException
   with `"IsFatal"` property)
5. Preserve `ANALYTICS_DEBUG` conditional logging
6. Set `TelemetryClient.Context.User.Id` and `Context.Properties` from injected sources
7. Implement `IDisposable` → `FlushAsync(CancellationToken.None)` or `Flush()`
8. Remove `IAnalyticsTransmitterSink.cs`
9. Remove `ITelemetryConfigurationHolder.cs`

### Phase 2 — VS layer cleanup

1. Remove `AppInsightsAnalyticsTransmitterSink.cs`
2. Remove `ApplicationInsightsConfigurationHolder.cs`
3. Remove `ReqnrollTelemetryContextInitializer.cs`
4. Remove VS-level `IEnableAnalyticsChecker.cs` (MEF-export Core's version directly)
5. Update VS `AnalyticsTransmitter.cs` (MEF export):
   - Read `InstrumentationKey` from embedded resource `InstrumentationKey.txt` (same resource,
     no format change)
   - Create `InMemoryChannel` with `SendingInterval = 30s`, `MaxTelemetryBufferCapacity = 250`
   - Create `new TelemetryConfiguration { InstrumentationKey = key, TelemetryChannel = channel }`
   - Create `new TelemetryClient(config)`
   - Set `Context.User.Id` from `FileUserIdStore`
   - Set `Context.Properties["Ide"]`, `["IdeVersion"]`, `["ExtensionVersion"]` from `IVersionProvider`
   - Pass client + deps to base `AnalyticsTransmitter`
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
   `TrackException` were called (or not). Verify `Context.User.Id` and
   `Context.Properties` are populated.

2. **New unit tests:**
   - Enable/disable gate still works
   - `IsNormalError` classification (normal vs fatal exceptions)
   - `FlushAsync()` on `Dispose()`
   - `ANALYTICS_DEBUG` conditional logging
   - No exception thrown when AppInsights transmission fails (catch-all)

3. **Check existing stubs compile:**
   - `StubAnalyticsTransmitter` (implements `IAnalyticsTransmitter`) — unchanged
   - `NullMonitoringService` — unchanged

### Phase 5 — Cleanup

1. Remove unused `using` statements (especially `Microsoft.VisualStudio.ApplicationInsights.*`)
2. Ensure `TelemetryClient` disposal is wired to VS package shutdown
3. Verify `REQNROLL_TELEMETRY_ENABLED=0` suppresses all telemetry (LSP + IDE)
4. Verify no exceptions escape the telemetry path

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
| `Should_SetContextProperties` | Verifies IDE metadata appears in `Context.Properties` |
| `Should_ClassifyNormalErrors` | `TimeoutException`, `TaskCanceledException`, `HttpRequestException` → still tracked |
| `Should_ClassifyFatalExceptions` | Unknown exception types → tracked with `IsFatal` property |
| `Should_FlushOnDispose` | `Dispose()` → calls `FlushAsync()` |
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
| Package | `Microsoft.ApplicationInsights` **2.23.0** | Tried-and-tested on netstandard2.0/net481; minimal deps (one transitive); near-identical API to current VS SDK wrapper |
| Where does `TelemetryClient` live? | `Reqnroll.IdeSupport.Common` (netstandard2.0) | Shared across all IDE extensions; VS Code / Rider can reuse without duplication |
| Who owns the NuGet dependency? | Only `Reqnroll.IdeSupport.Common.csproj` | Single point of truth; VS gets it transitively |
| How do LSP events reach AppInsights? | `telemetry/event` LSP notification → client deserializes → forwards to `AnalyticsTransmitter` | Protocol-conformant; server stays clean; single pipeline with IDE events |
| `WorkerService` package? | Not used | Pulls in 7+ collector/DependencyInjection packages we don't need |
| Telemetry channel | `InMemoryChannel` | Same channel as current code; no behavioural regression. `SendingInterval=30s`, `MaxTelemetryBufferCapacity=250`. No disk-backed `ServerTelemetryChannel` needed. |
| `TelemetryConfiguration.Active`? | **Not used** | Instance-based `TelemetryConfiguration` per IDE; no global state |
| `IContextInitializer`? | **Not used** | Properties set directly on `TelemetryClient.Context.Properties` at creation |
| Flush strategy | `Dispose()` calls `FlushAsync()`; channel auto-flushes every 30s during runtime | Manual flush-after-each-event defeated batching — now let the channel batch naturally |
| Instrumentation key storage | Embedded resource `InstrumentationKey.txt` (bare GUID) | No format change required; 2.x accepts bare `InstrumentationKey` |
| Namespace to remove | `Microsoft.VisualStudio.ApplicationInsights.*` | Replaced by `Microsoft.ApplicationInsights.*` |

---

## 7. Risk Assessment

| Risk | Impact | Mitigation |
|------|--------|------------|
| Some API names differ between VS SDK AppInsights and standard 2.x | Low | Core APIs (`TelemetryClient`, `EventTelemetry`, `ExceptionTelemetry`) are identical; `IContextInitializer` → replaced by direct `Context.Properties`; `FlushAndTransmitAsync` → `FlushAsync` |
| `TelemetryConfiguration.Active` use eliminated | Low | Instance-based `TelemetryConfiguration` is cleaner for multi-IDE scenario; each IDE creates its own |
| `IContextInitializer.Initialize(TelemetryContext)` needs porting | Low | Logic is trivial (set 4 string properties); move into VS `AnalyticsTransmitter` creation |
| Embedded `InstrumentationKey.txt` format stays? | None | Bare GUID works in 2.x; no format change needed |
| `TelemetryClient` disposal vs singleton lifetime | Low | Dispose on VS package close; 2.x auto-flush handles runtime |
| LSP `telemetry/event` handler not yet implemented | Low | Straightforward: receive `IDictionary<string, object?>`, construct `GenericEvent`, forward to `IAnalyticsTransmitter` |
| `Microsoft.ApplicationInsights` 2.23.0 deprecated status | Low | v2.23.0 is the latest non-deprecated 2.x release; stable, 146M+ downloads; migration to 3.x can happen later if/when needed |

---

## 8. Open Questions

1. **Where in the VS extension lifecycle should `TelemetryClient.FlushAsync()` be called?**
   The package `OnClose()` method is the obvious hook. The `TelemetryClient` instance should
   live as long as the VS extension process.

2. **`TelemetryClient` as a dependency** — injected via constructor (testable, recommended) or
   created internally in `AnalyticsTransmitter`? Constructor injection is cleaner — the VS
   MEF export creates the real client and injects it; tests inject a substitute.

3. **LSP `telemetry/event` schema** — what keys go in the notification params dictionary?
   Minimum: `eventName` (string) + `properties` (dictionary). Non-string property values must
   be serialisable safely.

4. **Should the server send telemetry before client connection?** For discovery/parser events,
   this is never an issue (they happen on request, after client is connected). A guard
   ("skip if no client") is easy to add.

5. **Deprecation concern: 2.x vs 3.x** — v2.23.0 is the last non-deprecated 2.x release.
   Microsoft has not deprecated it (unlike 2.20.0–2.22.0). If/when Microsoft eventually
   deprecates the 2.x line, migrating to 3.x is a future concern. For now, 2.23.0 is the
   safe and proven choice.
