# Refactor Analytics: Replace Custom AppInsights Client with Standard SDK

## Objective

Replace the current hand-rolled Application Insights telemetry client (using the VS SDK's
`Microsoft.VisualStudio.ApplicationInsights` namespace) with the standard
`Microsoft.ApplicationInsights` NuGet package (v3.x). The goal is to use a
prepackaged, maintained SDK rather than rolling our own client plumbing.

**Note:** `Microsoft.ApplicationInsights.WorkerService` (v3.1.2) requires
`net8.0+` and is **incompatible** with the current target (`net481`). This plan
uses the base `Microsoft.ApplicationInsights` package (v3.1.2), which targets
`net462` (covers `net481`).

---

## 1. Current Architecture (Inventory)

### 1.1 Source files in `src/Core/Reqnroll.IdeSupport.Common/Analytics/`

| File | Purpose | Touched by refactor? |
|------|---------|---------------------|
| `IAnalyticsEvent.cs` | Event contract: `EventName` + `Properties` | No — abstraction stays |
| `IAnalyticsTransmitter.cs` | Transmit event / exception / fatal-exception | No — abstraction stays |
| `IAnalyticsTransmitterSink.cs` | Transport abstraction (sink pattern) | **Likely removed** — replaced by direct `TelemetryClient` |
| `AnalyticsTransmitter.cs` | Gateway: checks enable, delegates to sink | **Yes** — route events to `TelemetryClient` instead of sink |
| `GenericEvent.cs` | Simple named event record | No — stays |
| `DiscoveryResultEvent.cs` | Commented out, LSP result event | No |
| `IEnableAnalyticsChecker.cs` | Opt-out via `REQNROLL_TELEMETRY_ENABLED` env var | No — stays |
| `EnableAnalyticsChecker` | (inline in same file) | No — stays |
| `IUserUniqueIdStore.cs` | User identity abstraction | No — stays (moves to `TelemetryClient.Context.User.Id`) |
| `IRegistryManager.cs` | Read/write install status from registry | No |
| `ReqnrollInstallationStatus.cs` | Install/usage tracking model | No |
| `GuidanceConfiguration.cs` | Usage milestone definitions | No |
| `GuidanceStep.cs` | Step model for guidance notifications | No |
| `GuidanceNotification.cs` | Enum for notification levels | No |
| `InstrumentationKey.txt` | Embedded resource with instrumentation key | **Replaced** — v3.x uses `ConnectionString` |

### 1.2 Source files in `src/VisualStudio/.../Analytics/`

| File | Purpose | Touched by refactor? |
|------|---------|---------------------|
| `AppInsightsAnalyticsTransmitterSink.cs` | **Key file** — implements `IAnalyticsTransmitterSink` using VS SDK `TelemetryClient` | **Replaced entirely** |
| `ApplicationInsightsConfigurationHolder.cs` | Reads key from embedded resource, sets `TelemetryConfiguration.Active` (global static) | **Replaced** — v3.x removes `TelemetryConfiguration.Active` |
| `ReqnrollTelemetryContextInitializer.cs` | Adds IDE/version/user properties to every telemetry item | **Replaced** — v3.x uses `TelemetryClient.Context.GlobalProperties` |
| `AnalyticsTransmitter.cs` | Thin MEF export wrapper over `Core.AnalyticsTransmitter` | No (or minimal — passes through) |
| `IEnableAnalyticsChecker.cs` | Thin MEF export wrapper | No |
| `FileUserIdStore.cs` | Persists userId to `%APPDATA%\Reqnroll\userid` | No — stays, but consumed differently |

### 1.3 Related files

| File | Location | Touched? |
|------|----------|---------|
| `MonitoringService.cs` | `.../Monitoring/MonitoringService.cs` | No — consumes `IAnalyticsTransmitter` |
| `NullMonitoringService.cs` | `LSP/.../Workspace/` | No |
| `VsWizardTelemetry.cs` | `Wizards/VsIntegration/` | No |
| `StubAnalyticsTransmitter.cs` | `tests/.../VsxStubs/` | No — test double stays |
| `AnalyticsTransmitterTests.cs` | `tests/.../Common.Tests/Analytics/` | **Add new tests** |
| `ITelemetryConfigurationHolder.cs` | `Core/.../ITelemetryConfigurationHolder.cs` | **Removed** — no longer needed |

### 1.4 Current data flow (simplified)

```
MonitoringService
  → IAnalyticsTransmitter (Core.AnalyticsTransmitter)
    → IEnableAnalyticsChecker (opt-out gate)
    → IAnalyticsTransmitterSink (AppInsightsAnalyticsTransmitterSink)
      → VS SDK TelemetryClient (Microsoft.VisualStudio.ApplicationInsights)
        → EventTelemetry / ExceptionTelemetry
        → Manual FlushAndTransmitAsync after each event
        → TelemetryConfiguration.Active (global singleton, key from embedded resource)
        → ReqnrollTelemetryContextInitializer (IContextInitializer on Active)
```

---

## 2. Target Architecture

### 2.1 Data flow (post-refactor)

```
MonitoringService
  → IAnalyticsTransmitter (Core.AnalyticsTransmitter)
    → IEnableAnalyticsChecker (opt-out gate)
    → Microsoft.ApplicationInsights.TelemetryClient
      → TrackEvent / TrackException
      → Auto-flush via SDK channel
      → TelemetryConfiguration (instance, not global singleton)
        → ConnectionString from embedded resource (or new format)
        → GlobalProperties set at client creation time
```

### 2.2 Key changes

| Current (VS SDK) | Target (Standard SDK) |
|-------------------|----------------------|
| `using Microsoft.VisualStudio.ApplicationInsights.*` | `using Microsoft.ApplicationInsights.*` |
| `TelemetryConfiguration.Active` (global static) | `TelemetryConfiguration.CreateDefault()` (instance) |
| `Active.InstrumentationKey = "..."` | `config.ConnectionString = "InstrumentationKey=...;"` |
| `Active.TelemetryChannel = new InMemoryChannel()` | Channel managed by SDK (auto-flush) |
| `Active.ContextInitializers.Add(...)` | `client.Context.GlobalProperties["key"] = "value"` |
| `new TelemetryClient()` (parameterless) | `new TelemetryClient(config)` (requires config) |
| `client.FlushAndTransmitAsync(cancellationToken)` | `client.Flush()` (synchronous, or auto-flush) |
| `EventTelemetry` / `ExceptionTelemetry` → `ISupportProperties` | Same API shape, different namespace |
| `IAnalyticsTransmitterSink` + `ITelemetryConfigurationHolder` | No intermediate abstraction — `AnalyticsTransmitter` owns `TelemetryClient` |

### 2.3 NuGet package changes

| Project | Current | Add | Remove |
|---------|---------|-----|--------|
| `VSSDKIntegration.csproj` (net481) | (no explicit AppInsights dependency — comes transitively from VS SDK) | `Microsoft.ApplicationInsights` v3.1.2 | — |

**(No change to `Reqnroll.IdeSupport.Common.csproj`** — it targets `netstandard2.0` and should not reference `Microsoft.ApplicationInsights`.)

---

## 3. Build Inventory (Files to Add / Remove / Modify)

### 3.1 Add

| File | Description |
|------|-------------|
| (none) — all changes are modifications of existing files |

### 3.2 Remove

| File | Reason |
|------|--------|
| `VS/.../Analytics/AppInsightsAnalyticsTransmitterSink.cs` | Replaced by direct `TelemetryClient` usage |
| `VS/.../Analytics/ApplicationInsightsConfigurationHolder.cs` | Config pattern changed; no global `TelemetryConfiguration.Active` |
| `VS/.../Analytics/ReqnrollTelemetryContextInitializer.cs` | Replaced by `TelemetryClient.Context.GlobalProperties` |
| `Core/.../ITelemetryConfigurationHolder.cs` | No longer needed — no configuration holder abstraction |
| `Core/.../IAnalyticsTransmitterSink.cs` | No longer needed — `TelemetryClient` is the sink |
| `Core/.../Analytics/InstrumentationKey.txt` | Replaced by connection string format (or updated in place) |

### 3.3 Modify

| File | What changes |
|------|-------------|
| `Core/.../Analytics/AnalyticsTransmitter.cs` | Remove `IAnalyticsTransmitterSink` dependency. Accept `IUserUniqueIdStore` and `IEnableAnalyticsChecker`. Create `TelemetryClient` internally. Route events to `TelemetryClient.TrackEvent()` / `TrackException()` / `Flush()`. Dispose `TelemetryClient` on shutdown. |
| `Core/.../Analytics/IAnalyticsTransmitter.cs` | No change needed (contract stays) |
| `Core/.../Analytics/IUserUniqueIdStore.cs` | No change needed |
| `VS/.../Analytics/AnalyticsTransmitter.cs` | Update MEF constructor to pass new dependencies |
| `VS/.../Analytics/IEnableAnalyticsChecker.cs` | No change needed |
| `VS/.../Analytics/FileUserIdStore.cs` | No change needed |
| `VS/.../Monitoring/MonitoringService.cs` | Remove `ITelemetryConfigurationHolder` dependency |
| `VS/.../VSSDKIntegration.csproj` | Add `<PackageReference Include="Microsoft.ApplicationInsights" Version="3.1.2" />` |
| `Core/.../Reqnroll.IdeSupport.Common.csproj` | Remove `EmbeddedResource Include="Analytics\InstrumentationKey.txt"` (if moved to VS project) |

**Important design decision:** The `TelemetryClient` should be created by the VSSDKIntegration layer (where the instrumentation key lives and where .NET Framework + MEF are available) and passed down, OR the `AnalyticsTransmitter` (in Core/Common, `netstandard2.0`) should create it. Since `Microsoft.ApplicationInsights` targets `net462` (ok for both), either approach works. **Recommended:** Keep the `TelemetryClient` creation in the VS project where configuration (connection string from embedded resource) naturally lives, and inject it as a dependency into `AnalyticsTransmitter`.

If we want to keep `AnalyticsTransmitter` in Core clean (no AppInsights dependency), then the sink pattern survives but is simplified: the VS project creates a `TelemetryClient` and injects it, and a thin wrapper translates `IAnalyticsEvent` → SDK calls. This is the less-invasive approach.

---

## 4. Implementation Plan (Phased)

### Phase A — Core changes (AnalyticsTransmitter in Core)

1. **Remove `IAnalyticsTransmitterSink.cs`** from Core
2. **Modify `AnalyticsTransmitter.cs`** to accept `IUserUniqueIdStore` + `IEnableAnalyticsChecker` + `TelemetryClient` (from `Microsoft.ApplicationInsights`)
3. Update `TransmitEvent` to call `_telemetryClient.TrackEvent()` directly with `EventTelemetry`
4. Update `TransmitExceptionEvent`/`TransmitFatalExceptionEvent` to call `_telemetryClient.TrackException()`
5. Set `TelemetryClient.Context.User.Id` and `GlobalProperties` from `IUserUniqueIdStore`
6. Add `Flush()` call or rely on SDK auto-flush
7. Remove `ITelemetryConfigurationHolder` from Core (if unused elsewhere)

### Phase B — VS layer cleanup

1. **Remove** `AppInsightsAnalyticsTransmitterSink.cs`
2. **Remove** `ApplicationInsightsConfigurationHolder.cs`
3. **Remove** `ReqnrollTelemetryContextInitializer.cs`
4. **Update** `AnalyticsTransmitter.cs` (the MEF export in VS project) to pass `IUserUniqueIdStore` + `IEnableAnalyticsChecker` + created `TelemetryClient`
5. **Create `TelemetryClient`** in the VS project using `TelemetryConfiguration.CreateDefault()` + `config.ConnectionString` loaded from embedded resource
6. **Remove `ITelemetryConfigurationHolder`** dependency from `MonitoringService`
7. **Add NuGet package** `Microsoft.ApplicationInsights` v3.1.2

### Phase C — Configuration

1. Update `InstrumentationKey.txt` (or create new resource) with connection string format: `InstrumentationKey=3fd018ff-819d-4685-a6e1-6f09bc98d20b`
2. Move the embedded resource to the VS project if desired (or keep in Core if that's cleaner)

### Phase D — Cleanup / Polish

1. Remove unused `using` statements
2. Remove `IAnalyticsTransmitterSink` interface from codebase
3. Ensure `TelemetryClient` disposal is handled (via VS extension shutdown)
4. Verify the `IsNormalError` classification logic is preserved
5. Verify the `ANALYTICS_DEBUG` conditional logging is preserved

---

## 5. Testing Plan

### 5.1 Existing tests that must still pass (no changes expected)

| Test file | Scope | Expected change |
|-----------|-------|-----------------|
| `tests/.../Common.Tests/Analytics/AnalyticsTransmitterTests.cs` | Gateway logic: enable/disable gate, event forwarding | **No change** — these test the `AnalyticsTransmitter` abstraction, which stays |

(It uses `Substitute.For<IAnalyticsTransmitterSink>()` as the test double. After the refactor, it would `Substitute.For<TelemetryClient>()` instead, or accept an injected `TelemetryClient`. The test assertions (`Received`, `DidNotReceive`) stay the same shape.)

### 5.2 Existing test doubles (stubs/fakes) — review needed

| File | Will it compile? | Fix needed? |
|------|-----------------|-------------|
| `tests/.../VsxStubs/StubAnalyticsTransmitter.cs` | Yes — implements `IAnalyticsTransmitter`, not `IAnalyticsTransmitterSink` | No |
| `LSP/.../NullMonitoringService.cs` | Yes — implements `IMonitoringService` | No |

### 5.3 New tests needed

| Test | What it covers |
|------|---------------|
| `AnalyticsTransmitterTests.Should_SetUserId_OnTelemetryClient` | Verify `TelemetryClient.Context.User.Id` is set from `IUserUniqueIdStore` |
| `AnalyticsTransmitterTests.Should_SetGlobalProperties_OnTelemetryClient` | Verify IDE/version properties are set |
| `AnalyticsTransmitterTests.Should_FlushOnShutdown` | Verify `Flush()` is called on `Dispose()` |
| `AnalyticsTransmitterTests.Should_NotThrow_WhenAppInsightsFails` | Verify existing catch-all safety net still works |
| `AnalyticsTransmitterTests.Should_ClassifyErrorsCorrectly` | Verify `IsNormalError` logic still used before `TrackException` |

### 5.4 Manual / integration tests

- Launch VS extension, verify telemetry appears in App Insights resource
- Test with `REQNROLL_TELEMETRY_ENABLED=0` set — verify no telemetry sent
- Test exception scenarios — verify `TrackException` is called with correct properties
- Verify no exceptions thrown during telemetry transmission (catch-all safety)

---

## 6. Risk Assessment

| Risk | Impact | Mitigation |
|------|--------|------------|
| `Microsoft.ApplicationInsights` v3.x API differs significantly from VS SDK AppInsights | High | Review migration guidance; API similar at the `TelemetryClient.TrackEvent/TrackException` level |
| `TelemetryConfiguration.Active` removed in v3.x | Medium | Use `TelemetryConfiguration.CreateDefault()` + instance-based config |
| `IContextInitializer` pattern removed in v3.x | Low | Use `TelemetryClient.Context.GlobalProperties` instead |
| Connection string is required in v3.x (throws if missing) | Medium | Must provide valid connection string; embedded resource updated to connection string format |
| `TelemetryClient` disposal conflicts with singleton usage | Low | Manage lifecycle in VS package shutdown |
| `WorkerService` package cannot be used (net8+ requirement) | Low | Use base `Microsoft.ApplicationInsights` package instead |

---

## 7. Open Questions

1. **Where should the `TelemetryClient` be created?** In the VS project (VSSDKIntegration) and injected into `AnalyticsTransmitter`, or directly in `AnalyticsTransmitter` (which is in Core/Common, `netstandard2.0`)? Keeping it in VS keeps the Core project free of AppInsights dependency and allows MEF DI to control the lifecycle.
2. **Connection string format** — should the embedded resource change from a bare GUID to `InstrumentationKey=3fd018ff-819d-4685-a6e1-6f09bc98d20b`, or should the config holder construct the connection string? The latter is more flexible (avoids hardcoding the ingestion endpoint).
3. **Flush strategy** — auto-flush (SDK default, ~30s) vs explicit flush after each event. Current code flushes after every event with a 1s timeout. For a VS extension, explicit flush on shutdown + periodic auto-flush is recommended.
