# Refactor Analytics: Replace Custom AppInsights Client with Standard SDK

## Objective

Replace the current hand-rolled Application Insights telemetry client (using the VS SDK's
`Microsoft.VisualStudio.ApplicationInsights` namespace) with the standard
`Microsoft.ApplicationInsights` NuGet package (v2.23.0). The goal is to use a
prepackaged, maintained SDK rather than rolling our own client plumbing.

**Note:** We use v2.23.0 of the base `Microsoft.ApplicationInsights` package, which
targets `netstandard2.0` natively with no OpenTelemetry dependencies
(only `System.Diagnostics.DiagnosticSource`). The v3.x line introduces
`Azure.Monitor.OpenTelemetry.Exporter` as a transitive dependency, which is
unnecessary for this VS extension.

---

## 1. Current Architecture (Inventory)

### 1.1 Source files in `src/Core/Reqnroll.IdeSupport.Common/Analytics/`

| File | Purpose | Touched by refactor? |
|------|---------|---------------------|
| `IAnalyticsEvent.cs` | Event contract: `EventName` + `Properties` | No — abstraction stays |
| `IAnalyticsTransmitter.cs` | Transmit event / exception / fatal-exception | No — abstraction stays |
| `IAnalyticsTransmitterSink.cs` | Transport abstraction (sink pattern) | **Removed** — replaced by direct `TelemetryClient` |
| `AnalyticsTransmitter.cs` | Gateway: checks enable, delegates to sink | **Yes** — route events to injected `TelemetryClient` instead of sink |
| `GenericEvent.cs` | Simple named event record | No — stays |
| `DiscoveryResultEvent.cs` | Commented out, LSP result event | No |
| `IEnableAnalyticsChecker.cs` | Opt-out via `REQNROLL_TELEMETRY_ENABLED` env var | No — stays |
| `EnableAnalyticsChecker` | (inline in same file) | No — stays |
| `IUserUniqueIdStore.cs` | User identity abstraction | No — stays |
| `IRegistryManager.cs` | Read/write install status from registry | No |
| `ReqnrollInstallationStatus.cs` | Install/usage tracking model | No |
| `GuidanceConfiguration.cs` | Usage milestone definitions | No |
| `GuidanceStep.cs` | Step model for guidance notifications | No |
| `GuidanceNotification.cs` | Enum for notification levels | No |
| `IGuidanceConfiguration.cs` | Guidance configuration interface | No |
| `InstrumentationKey.txt` | Embedded resource with instrumentation key | **Updated in place** — connection-string format |

### 1.2 Source files in `src/VisualStudio/.../VSSDKIntegration/Analytics/`

| File | Purpose | Touched by refactor? |
|------|---------|---------------------|
| `AppInsightsAnalyticsTransmitterSink.cs` | **Key file** — implements `IAnalyticsTransmitterSink` using VS SDK `TelemetryClient` | **Removed** |
| `ApplicationInsightsConfigurationHolder.cs` | Reads key from embedded resource, sets `TelemetryConfiguration.Active` (global static) | **Removed** — moving to instance-based config |
| `ReqnrollTelemetryContextInitializer.cs` | Adds IDE/version/user properties to every telemetry item | **Removed** — replaced by `TelemetryClient.Context.GlobalProperties` at creation time |
| `AnalyticsTransmitter.cs` | Thin MEF export wrapper over `Core.AnalyticsTransmitter` | **Rewritten** — creates/manages `TelemetryClient`, sets Context/GlobalProperties, implements `IAsyncDisposable` |
| `IEnableAnalyticsChecker.cs` | Thin MEF export wrapper | No |
| `FileUserIdStore.cs` | Persists userId to `%APPDATA%\Reqnroll\userid` | No — stays, consumed by VS `AnalyticsTransmitter` |

### 1.3 Related files

| File | Location | Touched? |
|------|----------|---------|
| `MonitoringService.cs` | `.../Monitoring/MonitoringService.cs` | **Yes** — remove `ITelemetryConfigurationHolder` dependency |
| `IMonitoringService.cs` | `Core/.../IMonitoringService.cs` | No |
| `NullMonitoringService.cs` | `LSP/.../Workspace/` | No |
| `VsWizardTelemetry.cs` | `Wizards/VsIntegration/` | No — consumes `IMonitoringService`, not `IAnalyticsTransmitter` directly |
| `StubAnalyticsTransmitter.cs` | `tests/.../VsxStubs/` | No — implements `IAnalyticsTransmitter`, not the sink |
| `AnalyticsTransmitterTests.cs` | `tests/.../Common.Tests/Analytics/` | **Rewritten** — substitute `TelemetryClient` instead of sink |
| `ITelemetryConfigurationHolder.cs` | `Core/.../ITelemetryConfigurationHolder.cs` | **Removed** |
| `StubIdeScope.cs` | `tests/.../VsxStubs/` | **Yes** — remove `ITelemetryConfigurationHolder` substitute |

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
      → Auto-flush via SDK channel (~30s)
      → Explicit Flush() on DisposeAsync()
      → TelemetryConfiguration (instance, no global singleton)
        → ConnectionString from embedded resource (in Core)
        → Context.User.Id + AccountId from IUserUniqueIdStore
        → GlobalProperties set at creation time by VS AnalyticsTransmitter
```

### 2.2 Key changes

| Current (VS SDK) | Target (Standard SDK v2.23.0) |
|-------------------|----------------------|
| `using Microsoft.VisualStudio.ApplicationInsights.*` | `using Microsoft.ApplicationInsights.*` |
| `TelemetryConfiguration.Active` (global static) | `new TelemetryConfiguration()` (instance) |
| `Active.InstrumentationKey = "..."` | `config.ConnectionString = "InstrumentationKey=...;"` |
| `Active.TelemetryChannel = new InMemoryChannel()` | Channel managed by SDK (auto-flush, ~30s) |
| `Active.ContextInitializers.Add(...)` | `client.Context.GlobalProperties["key"] = "value"` |
| `new TelemetryClient()` (parameterless, uses Active) | `new TelemetryClient(config)` (requires config) |
| `client.FlushAndTransmitAsync(cancellationToken)` | `client.Flush()` (synchronous, explicit shutdown) + auto-flush |
| `EventTelemetry` / `ExceptionTelemetry` → `ISupportProperties` | Same API shape, same namespace structure |
| `IAnalyticsTransmitterSink` + `ITelemetryConfigurationHolder` + `IReqnrollContextInitializer` | No intermediate abstractions — `AnalyticsTransmitter` owns `TelemetryClient` |
| Per-event fire-and-forget `FlushAndTransmitAsync` | SDK auto-flush (~30s) + lifecycle `Flush()` on `DisposeAsync()` |

### 2.3 NuGet package changes

| Project | Current | Add | Remove |
|---------|---------|-----|--------|
| `Reqnroll.IdeSupport.Common.csproj` (netstandard2.0) | (none) | `Microsoft.ApplicationInsights` v2.23.0 | — |
| `VSSDKIntegration.csproj` (net481) | (no explicit AppInsights dependency — comes transitively from VS SDK) | `Microsoft.ApplicationInsights` v2.23.0 | — |

Both need the package. `AnalyticsTransmitter` in Core uses `TelemetryClient` directly; the VS MEF export creates and configures it. v2.23.0 targets `netstandard2.0` natively, compatible with both `netstandard2.0` and `net481`.

---

## 3. Build Inventory (Files to Add / Remove / Modify)

### 3.1 Add

| File | Description |
|------|-------------|
| (none — all changes are modifications of existing files; the `TelemetryClient` creation lives in the rewritten VS `AnalyticsTransmitter`) |

### 3.2 Remove

| File | Reason |
|------|--------|
| `VS/.../Analytics/AppInsightsAnalyticsTransmitterSink.cs` | Replaced by direct `TelemetryClient` usage |
| `VS/.../Analytics/ApplicationInsightsConfigurationHolder.cs` | Config pattern changed; no global `TelemetryConfiguration.Active` |
| `VS/.../Analytics/ReqnrollTelemetryContextInitializer.cs` | Replaced by `TelemetryClient.Context.GlobalProperties` set in VS `AnalyticsTransmitter` |
| `Core/.../ITelemetryConfigurationHolder.cs` | No longer needed — no configuration holder abstraction |
| `Core/.../IAnalyticsTransmitterSink.cs` | No longer needed — `TelemetryClient` is the sink |

### 3.3 Modify

| File | What changes |
|------|-------------|
| `Core/.../Analytics/AnalyticsTransmitter.cs` | Remove `IAnalyticsTransmitterSink` dependency. Accept injected `TelemetryClient` + `IEnableAnalyticsChecker`. Route events to `TelemetryClient.TrackEvent()` / `TrackException()`. Implement `IAsyncDisposable` for `Flush()` + `Dispose()`. Preserve `IsNormalError`, `ANALYTICS_DEBUG`, catch-all safety. |
| `Core/.../Reqnroll.IdeSupport.Common.csproj` | Add `<PackageReference Include="Microsoft.ApplicationInsights" Version="2.23.0" />`. Keep `EmbeddedResource` for `InstrumentationKey.txt` (updated in place). |
| `Core/.../Analytics/InstrumentationKey.txt` | Change content from bare GUID to `InstrumentationKey=3fd018ff-819d-4685-a6e1-6f09bc98d20b` |
| `VS/.../Analytics/AnalyticsTransmitter.cs` | **Rewritten.** No longer inheriting by passing sink. Now: imports `IUserUniqueIdStore`, `IVersionProvider`, `IEnableAnalyticsChecker` via MEF; creates `TelemetryClient` with config, Context.User.Id/AccountId, and GlobalProperties; passes to base; implements `IAsyncDisposable`. |
| `VS/.../Monitoring/MonitoringService.cs` | Remove `ITelemetryConfigurationHolder` from constructor and field. The config holder's `ApplyConfiguration()` call is removed — replaced by instance-based config in the VS `AnalyticsTransmitter`. |
| `VS/.../VSSDKIntegration.csproj` | Add `<PackageReference Include="Microsoft.ApplicationInsights" Version="2.23.0" />` |
| `tests/.../Common.Tests/Analytics/AnalyticsTransmitterTests.cs` | **Rewritten.** Substitute `TelemetryClient` instead of `IAnalyticsTransmitterSink`. Assert `TrackEvent`/`TrackException`/`Flush` calls. Same test-case semantics. |
| `tests/.../VsxStubs/StubIdeScope.cs` | Remove `Substitute.For<ITelemetryConfigurationHolder>()` from `MonitoringService` construction. |

---

## 4. Implementation Plan (Phased)

### Phase A — Core changes (AnalyticsTransmitter in Core)

1. **Add NuGet package** `Microsoft.ApplicationInsights` v2.23.0 to `Reqnroll.IdeSupport.Common.csproj`
2. **Remove `IAnalyticsTransmitterSink.cs`** from Core
3. **Remove `ITelemetryConfigurationHolder.cs`** from Core
4. **Rewrite `AnalyticsTransmitter.cs`** in Core:
   - Constructor: `(TelemetryClient telemetryClient, IEnableAnalyticsChecker enableAnalyticsChecker, IDeveroomLogger? logger = null)`
   - `TransmitEvent` → `_telemetryClient.TrackEvent(new EventTelemetry(analyticsEvent.EventName) { ... })`, copy `Properties` via `ISupportProperties`
   - `TransmitException` → `_telemetryClient.TrackException(new ExceptionTelemetry(exception) { ... })`
   - Preserve `IsNormalError` classification logic
   - Preserve `[Conditional("ANALYTICS_DEBUG")]` dump methods
   - Preserve catch-all safety (`try/catch` around every transmit)
   - Implement `IAsyncDisposable`:
     ```csharp
     public async ValueTask DisposeAsync()
     {
         _telemetryClient.Flush();
         await Task.Delay(1000); // allow in-flight transmission
         _telemetryClient.Dispose();
     }
     ```
5. **Update `InstrumentationKey.txt`** content to connection-string format

### Phase B — VS layer cleanup

1. **Remove** `AppInsightsAnalyticsTransmitterSink.cs`
2. **Remove** `ApplicationInsightsConfigurationHolder.cs`
3. **Remove** `ReqnrollTelemetryContextInitializer.cs`
4. **Add NuGet package** `Microsoft.ApplicationInsights` v2.23.0 to `VSSDKIntegration.csproj`
5. **Rewrite VS `AnalyticsTransmitter.cs`** (MEF export in VSSDKIntegration):
   ```csharp
   [Export(typeof(IAnalyticsTransmitter))]
   public class AnalyticsTransmitter : CoreAnalyticsTransmitter, IAsyncDisposable
   {
       [ImportingConstructor]
       public AnalyticsTransmitter(
           IEnableAnalyticsChecker enableAnalyticsChecker,
           IUserUniqueIdStore userUniqueIdStore,
           IVersionProvider versionProvider,
           DeveroomCompositeLogger? logger = null)
           : base(CreateClient(userUniqueIdStore, versionProvider), enableAnalyticsChecker, logger) { }

       private static TelemetryClient CreateClient(
           IUserUniqueIdStore userStore, IVersionProvider versionProvider)
       {
           var config = new TelemetryConfiguration();
           var stream = typeof(CoreAnalyticsTransmitter).Assembly
               .GetManifestResourceStream(
                   "Reqnroll.IdeSupport.Common.Analytics.InstrumentationKey.txt");
           using var reader = new StreamReader(stream!);
           config.ConnectionString = reader.ReadLine()!;

           var client = new TelemetryClient(config);
           client.Context.User.Id = userStore.GetUserId();
           client.Context.User.AccountId = userStore.GetUserId();
           client.Context.GlobalProperties["Ide"] = "Microsoft Visual Studio";
           client.Context.GlobalProperties["IdeVersion"] = versionProvider.GetVsVersion();
           client.Context.GlobalProperties["ExtensionVersion"] = versionProvider.GetExtensionVersion();
           return client;
       }
   }
   ```
   > Note: v2.23.0 uses `new TelemetryConfiguration()` rather than
   > `TelemetryConfiguration.CreateDefault()` (which tries to load an
   > `ApplicationInsights.config` file). We construct an empty config and
   > set `ConnectionString` directly.
6. **Update `MonitoringService`** — remove `ITelemetryConfigurationHolder telemetryConfigurationHolder` from constructor; remove `telemetryConfigurationHolder.ApplyConfiguration()` call body
7. **Update `StubIdeScope`** — replace `Substitute.For<ITelemetryConfigurationHolder>()` with nothing; change to `new MonitoringService(AnalyticsTransmitter)`

### Phase C — Configuration

1. Update `InstrumentationKey.txt` content:
   ```
   InstrumentationKey=3fd018ff-819d-4685-a6e1-6f09bc98d20b
   ```
2. The resource stays in the Core project as an embedded resource (no move). The VS layer reads it via `typeof(AnalyticsTransmitter).Assembly.GetManifestResourceStream(...)`.

### Phase D — Tests

1. **Rewrite `AnalyticsTransmitterTests.cs`**:
   - `CreateSut()` creates `Substitute.For<TelemetryClient>()` instead of `Substitute.For<IAnalyticsTransmitterSink>()`
   - Same test cases: disable → `DidNotReceive().TrackEvent(...)`, enable → `Received().TrackEvent(...)`, event name passthrough
   - Add new tests:
     - `Should_FlushOnDispose` — verify `Flush()` is called during `DisposeAsync()`
     - `Should_NotThrow_WhenAppInsightsFails` — verify catch-all safety net
   - The assertion shape (`Received`, `DidNotReceive`) stays identical because `TelemetryClient` has virtual `TrackEvent`/`TrackException`/`Flush` methods

2. **Verify `StubAnalyticsTransmitter` compiles** — it implements `IAnalyticsTransmitter`, not the sink; no change needed.

### Phase E — Cleanup / Polish

1. Remove unused `using` statements
2. Verify `StubIdeScope` compiles with removed `ITelemetryConfigurationHolder`
3. Verify VS package shutdown calls `DisposeAsync()` on the `MonitoringService` (which forwards to `AnalyticsTransmitter`)
4. Verify the `IsNormalError` classification logic is preserved in Core
5. Verify the `ANALYTICS_DEBUG` conditional logging is preserved

---

## 5. Testing Plan

### 5.1 Existing tests that must still pass

| Test file | Scope | Expected change |
|-----------|-------|-----------------|
| `tests/.../Common.Tests/Analytics/AnalyticsTransmitterTests.cs` | Gateway logic: enable/disable gate, event forwarding | **Rewritten** — substitute `TelemetryClient` instead of `IAnalyticsTransmitterSink`; same test case semantics |
| `tests/.../VsxStubs/StubAnalyticsTransmitter.cs` | Test double for `IAnalyticsTransmitter` | No change — implements `IAnalyticsTransmitter`, not sink |

### 5.2 Existing call sites / stubs — review needed

| File | Will it compile? | Fix needed? |
|------|-----------------|-------------|
| `tests/.../VsxStubs/StubIdeScope.cs` | **No** — constructs `MonitoringService` with `Substitute.For<ITelemetryConfigurationHolder>()` | **Yes** — remove the substitute param |
| `tests/.../VsxStubs/StubAnalyticsTransmitter.cs` | Yes — implements `IAnalyticsTransmitter` | No |
| `LSP/.../NullMonitoringService.cs` | Yes — implements `IMonitoringService` | No |

### 5.3 New tests needed

| Test | What it covers |
|------|---------------|
| `AnalyticsTransmitterTests.Should_FlushOnDispose` | Verify `Flush()` is called during `DisposeAsync()` |
| `AnalyticsTransmitterTests.Should_NotThrow_WhenAppInsightsFails` | Verify existing catch-all safety net still works |
| (Existing tests already cover enable/disable gate and event name passthrough — same assertions, new substitute) |

### 5.4 Manual / integration tests

- Launch VS extension, verify telemetry appears in App Insights resource
- Test with `REQNROLL_TELEMETRY_ENABLED=0` set — verify no telemetry sent
- Test exception scenarios — verify `TrackException` is called with correct properties
- Verify no exceptions thrown during telemetry transmission (catch-all safety)
- Verify flush on VS extension shutdown (check that no events are lost on close)

---

## 6. Risk Assessment

| Risk | Impact | Mitigation |
|------|--------|------------|
| `Microsoft.ApplicationInsights` v2.x API differs from VS SDK AppInsights | Medium | API is near-identical at the `TelemetryClient.TrackEvent/TrackException` level; same namespace structure |
| `TelemetryConfiguration.Active` deprecated but still present in v2.x | Low | We're deliberately moving away from it to instance-based config, not because it's removed |
| Connection string is required (throws if missing) | Medium | Must provide valid connection string; embedded resource updated to connection-string format |
| `TelemetryClient` disposal must be managed | Low | Implement `IAsyncDisposable` on VS `AnalyticsTransmitter`, forwarded through `MonitoringService` |
| Adding `Microsoft.ApplicationInsights` v2.23.0 to `netstandard2.0` project | Low | v2.23.0 targets `netstandard2.0` natively; only dependency is `System.Diagnostics.DiagnosticSource` ≥ 5.0.0 (already present) |
| VS `AnalyticsTransmitter` needs `StreamReader` — currently no `using System.IO` in that file | Low | Add the using directive (minor) |

---

## 7. Resolved Design Decisions

| Question | Decision |
|----------|----------|
| Where should `TelemetryClient` be created? | In the VS project (VSSDKIntegration), inside the MEF-exported `AnalyticsTransmitter`. Injected into Core's `AnalyticsTransmitter` via constructor. |
| Does Core get an AppInsights dependency? | **Yes** — `Microsoft.ApplicationInsights` v2.23.0 is added to `Common.csproj`. The `AnalyticsTransmitter` in Core accepts/injects `TelemetryClient`. |
| Which version of the AppInsights SDK? | **v2.23.0** — targets `netstandard2.0` natively, minimal deps, no OpenTelemetry transitives. |
| Who owns GlobalProperties and User.Id setup? | The VS `AnalyticsTransmitter` creates the `TelemetryClient` and sets `Context.User.Id`, `AccountId`, and all `GlobalProperties` from MEF-injected `IUserUniqueIdStore` and `IVersionProvider`. |
| Should `AccountId` be preserved? | **Yes** — set to `User.Id` value (same as current sink did). |
| Flush strategy? | **Both** — SDK auto-flush (~30s via `InMemoryChannel`) + explicit `Flush()` + 1s delay on `DisposeAsync()`. No per-event fire-and-forget. |
| Where does `InstrumentationKey.txt` live? | Stays in Core as an embedded resource. VS reads it via `typeof(AnalyticsTransmitter).Assembly.GetManifestResourceStream(...)`. |
| Connection string format? | Embedded resource content changes from bare GUID to `InstrumentationKey=3fd018ff-819d-4685-a6e1-6f09bc98d20b`. |
| Scope of this plan? | Expanded to cover `StubIdeScope`, VS package lifecycle disposal, and all test impacts. |
