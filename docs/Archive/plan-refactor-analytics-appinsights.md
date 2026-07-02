# Refactor Analytics: Replace Custom AppInsights Client with Standard SDK

**Status: implemented.** This document records the as-built design.

## Objective

Two related changes:

1. Replace the hand-rolled Application Insights telemetry client (the VS SDK's
   `Microsoft.VisualStudio.ApplicationInsights` namespace plus our sink /
   configuration-holder / context-initializer plumbing) with the standard
   `Microsoft.ApplicationInsights` NuGet package (v2.23.0).
2. **Place the concrete transmitter where it belongs for a multi-IDE solution.**
   The `Microsoft.ApplicationInsights` dependency and the `TelemetryClient`-owning
   `AnalyticsTransmitter` live **only in the VS host layer (VSSDKIntegration)**, not in
   `Core/Common`. `Common` keeps just the IDE-neutral *contracts*.

**Note on package version:** We use v2.23.0 of the base `Microsoft.ApplicationInsights`
package, which targets `netstandard2.0` natively with no OpenTelemetry dependencies
(only `System.Diagnostics.DiagnosticSource`). The v3.x line introduces
`Azure.Monitor.OpenTelemetry.Exporter` as a transitive dependency, which is
unnecessary for this extension.

---

## 0. Why the transmitter is host-side, not in the LSP server

This is the load-bearing architectural decision; everything below follows from it.

DRY intuition says "implement analytics once, in the shared component" — i.e. the LSP
server, since it is the only .NET component common to all three target IDEs (VS, VSCode,
Rider). That intuition is **wrong here**, for two reasons:

1. **Timing.** Several analytics events are raised *before any LSP server exists*: the
   Welcome wizard on first install, the New Project wizard, install/upgrade/usage
   lifecycle events. VS loads the language server lazily — only after a feature file is
   opened for edit. A server-side transmitter could never see these pre-LSP events.
2. **The DRY calculus actually inverts.** Pre-LSP events therefore *force* a host-side
   transmitter in every IDE regardless. Once that exists, routing the remaining
   in-session events through that same host-side sink is nearly free (it is the existing
   `TelemetryEventInterceptor`). Adding a *second*, server-side transmitter for in-session
   events would mean AppInsights + consent + user-id + IDE-context plumbing in **both** the
   server **and** each host (4 sinks, not 3), plus synchronizing consent into the server.

So transmission is host-side per IDE — VS in .NET (here), VSCode in TypeScript, Rider on
the JVM — joining logging, wizards, and LSP-client glue as the per-IDE "repeat
implementations." Critically, **only the transmission *sink* is duplicated**; in-session
event *origination* stays single-sourced in the server.

### Event categories

| | Originates in | Examples | Transmitted by |
|---|---|---|---|
| **A — pre-LSP / lifecycle** | host process (no server yet) | Welcome wizard, New Project wizard, install/upgrade, days-of-usage | host transmitter directly |
| **B — in-session / editing** | LSP server | feature-file open, command usage, server-side errors | server emits `telemetry/event` → host transmitter forwards |

### Consequence for `Common` and the server

Because the server **never transmits** (`NullMonitoringService` is a no-op;
`ILspTelemetryService.SendEvent` only emits notifications), `Microsoft.ApplicationInsights`
has no business in `Common` — it would be a compile-time stowaway dragged into the
`net10.0` server's dependency graph for nothing. Hence the move: AppInsights and the
concrete transmitter live in `VSSDKIntegration`; `Common` keeps the contracts.

---

## 1. Architecture (as built)

### 1.1 `src/Core/Reqnroll.IdeSupport.Common/Analytics/` — contracts only, **no AppInsights**

| File | Purpose | Outcome |
|------|---------|---------|
| `IAnalyticsEvent.cs` | Event contract: `EventName` + `Properties` | Stays — IDE-neutral |
| `IAnalyticsTransmitter.cs` | Transmit event / exception / fatal-exception | Stays — IDE-neutral |
| `GenericEvent.cs` | Simple named event record | Stays |
| `IEnableAnalyticsChecker.cs` + inline `EnableAnalyticsChecker` | Opt-out via `REQNROLL_TELEMETRY_ENABLED` | Stays |
| `IUserUniqueIdStore.cs` | User identity abstraction | Stays |
| `IRegistryManager.cs`, `ReqnrollInstallationStatus.cs`, `Guidance*.cs` | Install/usage + guidance models | Stay |
| `AnalyticsTransmitter.cs` | Concrete `TelemetryClient`-based transmitter | **Removed from Core** — moved to VSSDKIntegration |
| `InstrumentationKey.txt` | Embedded AppInsights connection string | **Removed from Core** — moved to VSSDKIntegration |
| `IAnalyticsTransmitterSink.cs` | Old transport abstraction | **Removed** — `TelemetryClient` is the sink |
| `ITelemetryConfigurationHolder.cs` | Old global-config holder | **Removed** |

### 1.2 `src/VisualStudio/.../VSSDKIntegration/Analytics/` — the only AppInsights consumer

| File | Purpose | Outcome |
|------|---------|---------|
| `AnalyticsTransmitter.cs` | **MEF-exported concrete transmitter.** Owns `TelemetryClient`; contains the transmission logic (event/exception shaping, `IsNormalError`, `ANALYTICS_DEBUG` dumps, catch-all safety) and VS client construction (connection string, `Context.User`, `GlobalProperties`); implements `IAsyncDisposable`. Public `[ImportingConstructor]` for MEF + `internal` ctor as a test seam. | **Added** (absorbs the former Core base class) |
| `InstrumentationKey.txt` | Embedded resource, connection-string format | **Added** (moved from Core) |
| `IEnableAnalyticsChecker.cs` | Thin MEF export over Core's `EnableAnalyticsChecker` | Stays |
| `FileUserIdStore.cs` | Persists userId to `%APPDATA%\Reqnroll\userid` | Stays |
| `AppInsightsAnalyticsTransmitterSink.cs`, `ApplicationInsightsConfigurationHolder.cs`, `ReqnrollTelemetryContextInitializer.cs` | Old VS SDK plumbing | **Removed** |

### 1.3 Related files

| File | Location | Outcome |
|------|----------|---------|
| `MonitoringService.cs` | `VSSDKIntegration/Monitoring/` | Ctor takes `IAnalyticsTransmitter` only; `ITelemetryConfigurationHolder` dependency removed |
| `IMonitoringService.cs` | `Core/` | Unchanged |
| `NullMonitoringService.cs` | `LSP/.../Workspace/` | Unchanged — server-side no-op (telemetry not collected server-side) |
| `ILspTelemetryService.cs` | `LSP/.../Telemetry/` | Unchanged — emits `telemetry/event` notifications only |
| `TelemetryEventInterceptor.cs` | `VS Extension/LspInterception/` | Unchanged — receives `telemetry/event`, forwards to `IAnalyticsTransmitter` |
| `ReqnrollPluginPackage.cs` | `VS Extension/` | Holds `IAnalyticsTransmitter` field; calls `DisposeAsync()` from `Dispose(bool)` |
| `StubAnalyticsTransmitter.cs` | `tests/.../VsxStubs/` | Unchanged — implements `IAnalyticsTransmitter` |
| `AnalyticsTransmitterTests.cs` | **Moved** `Common.Tests` → `Reqnroll.VisualStudio.Tests/Analytics/` | Tests the VS transmitter via its internal ctor + an in-memory channel |

### 1.4 Data flow (as built)

```
(A) Wizards / package lifecycle  ── host, pre-LSP ──┐
      → IMonitoringService (VSSDKIntegration)        │
          → IAnalyticsTransmitter ──────────────────┤
                                                     │
(B) LSP server, in-session                           │
      → ILspTelemetryService.SendEvent               │
          → telemetry/event notification             │
              → VS TelemetryEventInterceptor ────────┤
                                                     ▼
                 VSSDKIntegration.AnalyticsTransmitter   (single host-side sink)
                   → IEnableAnalyticsChecker (opt-out gate)
                   → Microsoft.ApplicationInsights.TelemetryClient
                       → TrackEvent / TrackException
                       → ConnectionString from embedded resource (in VSSDKIntegration)
                       → Context.User.Id/AccountId (IUserUniqueIdStore)
                       → GlobalProperties: Ide / IdeVersion / ExtensionVersion (IVersionProvider)
                       → SDK auto-flush (~30s) + explicit Flush() on DisposeAsync()
                           (DisposeAsync wired from ReqnrollPluginPackage.Dispose)
```

---

## 2. Standard-SDK API mapping (reference)

| Old (VS SDK `Microsoft.VisualStudio.ApplicationInsights`) | New (`Microsoft.ApplicationInsights` v2.23.0) |
|-------------------|----------------------|
| `using Microsoft.VisualStudio.ApplicationInsights.*` | `using Microsoft.ApplicationInsights.*` |
| `TelemetryConfiguration.Active` (global static) | `new TelemetryConfiguration()` (instance) |
| `Active.InstrumentationKey = "..."` | `config.ConnectionString = "InstrumentationKey=...;"` |
| `Active.TelemetryChannel = new InMemoryChannel()` | Channel managed by SDK (auto-flush, ~30s) |
| `Active.ContextInitializers.Add(...)` | `client.Context.GlobalProperties["key"] = "value"` |
| `new TelemetryClient()` (uses `Active`) | `new TelemetryClient(config)` |
| `client.FlushAndTransmitAsync(ct)` per event | `client.Flush()` on shutdown + SDK auto-flush |
| `IAnalyticsTransmitterSink` + `ITelemetryConfigurationHolder` + `IReqnrollContextInitializer` | None — `AnalyticsTransmitter` owns `TelemetryClient` directly |

> v2.23.0 uses `new TelemetryConfiguration()` rather than `TelemetryConfiguration.CreateDefault()`
> (which tries to load an `ApplicationInsights.config` file). We construct an empty config and
> set `ConnectionString` directly.

### NuGet package placement

| Project | `Microsoft.ApplicationInsights` |
|---------|---------------------------------|
| `Reqnroll.IdeSupport.Common.csproj` (netstandard2.0) | **Not referenced** — contracts only; keeps it out of the LSP server graph |
| `VSSDKIntegration.csproj` (net481) | **Referenced** (v2.23.0) — the only .NET component that transmits |

---

## 3. Build inventory (changes that were made)

### 3.1 Removed

| File | Reason |
|------|--------|
| `Core/.../Analytics/AnalyticsTransmitter.cs` | Concrete transmitter moved to VSSDKIntegration |
| `Core/.../Analytics/InstrumentationKey.txt` | Embedded key moved to VSSDKIntegration |
| `Core/.../Analytics/IAnalyticsTransmitterSink.cs` | `TelemetryClient` is the sink |
| `Core/.../ITelemetryConfigurationHolder.cs` | No global configuration holder |
| `VS/.../Analytics/AppInsightsAnalyticsTransmitterSink.cs` | Replaced by direct `TelemetryClient` usage |
| `VS/.../Analytics/ApplicationInsightsConfigurationHolder.cs` | No global `TelemetryConfiguration.Active` |
| `VS/.../Analytics/ReqnrollTelemetryContextInitializer.cs` | Replaced by `Context.GlobalProperties` set at client creation |
| `tests/.../Common.Tests/Analytics/AnalyticsTransmitterTests.cs` | Moved to the VS test project |

### 3.2 Added / moved

| File | Description |
|------|-------------|
| `VS/.../Analytics/AnalyticsTransmitter.cs` | Concrete MEF-exported transmitter (logic + client construction merged into one class) |
| `VS/.../Analytics/InstrumentationKey.txt` | Embedded resource (connection-string format) |
| `tests/.../Reqnroll.VisualStudio.Tests/Analytics/AnalyticsTransmitterTests.cs` | Moved; tests via internal ctor + `InMemoryTelemetryChannel` |

### 3.3 Modified

| File | What changed |
|------|-------------|
| `Core/.../Reqnroll.IdeSupport.Common.csproj` | **Removed** `Microsoft.ApplicationInsights` PackageReference **and** the `InstrumentationKey.txt` `EmbeddedResource` |
| `VS/.../VSSDKIntegration.csproj` | Keeps `Microsoft.ApplicationInsights` v2.23.0; **added** `EmbeddedResource Include="Analytics\InstrumentationKey.txt"` and `InternalsVisibleTo` for `Reqnroll.VisualStudio.Tests` (internal test-seam ctor) |
| `VS/.../Monitoring/MonitoringService.cs` | Constructor reduced to `(IAnalyticsTransmitter)`; `ITelemetryConfigurationHolder` + `ApplyConfiguration()` removed |
| `VS Extension/ReqnrollPluginPackage.cs` | Resolves and stores `IAnalyticsTransmitter`; disposes it from `Dispose(bool)` |
| `tests/.../VsxStubs/StubIdeScope.cs` | Constructs `MonitoringService` without the `ITelemetryConfigurationHolder` substitute |

---

## 4. The VS transmitter (as built)

`VSSDKIntegration/Analytics/AnalyticsTransmitter.cs` — one class, two constructors:

```csharp
[Export(typeof(IAnalyticsTransmitter))]
public class AnalyticsTransmitter : IAnalyticsTransmitter, IAsyncDisposable
{
    [ImportingConstructor]                       // production: MEF builds the real client
    public AnalyticsTransmitter(
        IEnableAnalyticsChecker enableAnalyticsChecker,
        IUserUniqueIdStore userUniqueIdStore,
        IVersionProvider versionProvider,
        Reqnroll.IdeSupport.VisualStudio.Diagnostics.DeveroomCompositeLogger? logger = null)
        : this(CreateClient(userUniqueIdStore, versionProvider), enableAnalyticsChecker, logger) { }

    internal AnalyticsTransmitter(                // test seam: inject a channel-backed client
        TelemetryClient telemetryClient,
        IEnableAnalyticsChecker enableAnalyticsChecker,
        IDeveroomLogger? logger = null) { /* assign fields */ }

    private static TelemetryClient CreateClient(IUserUniqueIdStore userStore, IVersionProvider versionProvider)
    {
        var config = new TelemetryConfiguration();
        var assembly = typeof(AnalyticsTransmitter).Assembly;
        var resourceName = assembly.GetManifestResourceNames()
            .Single(n => n.EndsWith("InstrumentationKey.txt", StringComparison.Ordinal));
        using var stream = assembly.GetManifestResourceStream(resourceName);
        using var reader = new StreamReader(stream!);
        config.ConnectionString = reader.ReadLine();
        var client = new TelemetryClient(config);
        client.Context.User.Id = userStore.GetUserId();
        client.Context.User.AccountId = userStore.GetUserId();
        client.Context.GlobalProperties["Ide"] = "Microsoft Visual Studio";
        client.Context.GlobalProperties["IdeVersion"] = versionProvider.GetVsVersion();
        client.Context.GlobalProperties["ExtensionVersion"] = versionProvider.GetExtensionVersion();
        return client;
    }

    // TransmitEvent / TransmitExceptionEvent / TransmitFatalExceptionEvent / IsNormalError
    // / [Conditional("ANALYTICS_DEBUG")] dumps / catch-all safety  — unchanged from the
    // former Core implementation.

    public async ValueTask DisposeAsync()
    {
        _telemetryClient.Flush();
        await Task.Delay(1000);  // allow in-flight transmission
    }
}
```

> The embedded resource is located by suffix (`EndsWith("InstrumentationKey.txt")`) rather
> than a hard-coded `RootNamespace`-derived name, so it survives namespace/folder changes.

### Disposal wiring (`ReqnrollPluginPackage`)

MEF does not call `DisposeAsync()` on its parts, so the package does it explicitly:

```csharp
private IAnalyticsTransmitter? _analyticsTransmitter;

protected override async Task InitializeAsync(CancellationToken ct, IProgress<ServiceProgressData> progress)
{
    await base.InitializeAsync(ct, progress);
    // Resolve early — before the 7s welcome delay — so disposal is always wired.
    var sp = await GetServiceAsync(typeof(SComponentModel)) as IServiceProvider;
    _analyticsTransmitter = VsUtils.ResolveMefDependency<IAnalyticsTransmitter>(sp);
    // ... rest of existing init
}

protected override void Dispose(bool disposing)
{
    if (disposing && _analyticsTransmitter is IAsyncDisposable d)
        ThreadHelper.JoinableTaskFactory.Run(() => d.DisposeAsync().AsTask());
    base.Dispose(disposing);
}
```

The 1-second `Task.Delay` in `DisposeAsync` runs inside `JoinableTaskFactory.Run`, blocking
package dispose long enough for in-flight HTTP to complete without deadlocking the VS UI thread.

---

## 5. Testing (as built)

`AnalyticsTransmitterTests` lives in **`Reqnroll.VisualStudio.Tests/Analytics/`** (not
`Common.Tests`, which no longer references AppInsights). It constructs the SUT through the
**internal test-seam ctor** (exposed via `InternalsVisibleTo`), injecting a real
`TelemetryClient` whose `TelemetryChannel` is an in-memory fake:

```csharp
private VsAnalyticsTransmitter CreateSut()
{
    _enableAnalyticsCheckerStub = Substitute.For<IEnableAnalyticsChecker>();
    _telemetryChannel = new InMemoryTelemetryChannel();
    var config = new TelemetryConfiguration
    {
        TelemetryChannel = _telemetryChannel,
        ConnectionString = $"InstrumentationKey={Guid.NewGuid():N}"
    };
    return new VsAnalyticsTransmitter(new TelemetryClient(config), _enableAnalyticsCheckerStub);
}
```

> Why a channel fake rather than `Substitute.For<TelemetryClient>()`: substituting the client
> would mock away the very SDK behavior under test. A real `TelemetryClient` + in-memory
> channel exercises the actual `TrackEvent`/`TrackException`/`Flush` path and lets tests assert
> exactly what was sent (`SentTelemtries`) and that flush occurred (`IsFlushed`).

| Test | Covers |
|------|--------|
| `Should_NotSendAnalytics_WhenDisabled` | Opt-out gate — nothing sent |
| `Should_SendAnalytics_WhenEnabled` | One telemetry item sent when enabled |
| `Should_TransmitEvents` (3 cases) | Event name passthrough as `EventTelemetry` |
| `Should_FlushOnDispose` | `Flush()` called during `DisposeAsync()` |
| `Should_NotThrow_WhenAppInsightsFails` | Catch-all safety — channel throws, no exception escapes |

All 7 pass. `StubAnalyticsTransmitter` and `NullMonitoringService` are unaffected (they
implement the Core contracts, which did not change).

### Manual / integration checks

- Launch VS extension; verify telemetry appears in the App Insights resource.
- `REQNROLL_TELEMETRY_ENABLED=0` → no telemetry sent.
- Exception scenarios → `TrackException` with correct properties.
- Flush on VS shutdown → no events lost on close.

---

## 6. The DiagnosticSource binding redirect (VS-scoped)

`Microsoft.ApplicationInsights` 2.23.0's **net46** build references
`System.Diagnostics.DiagnosticSource 5.0.0.0` in its manifest, but the VS SDK forces
9.0.0.0. On **.NET Framework (the VS host)** the Fusion loader rejects the mismatch, so the
VS extension ships a binding redirect:

- `Extension/BindingRedirects.pkgdef` — `RuntimeConfiguration\dependentAssembly\bindingRedirection`
  mapping `0.0.0.0-8.0.0.0 → 9.0.0.0`, declared as a `Microsoft.VisualStudio.VsPackage`
  asset in `source.extension.vsixmanifest`.
- `Extension.csproj` target `IncludeDiagnosticSourceInVsix` — forces the DLL into the VSIX
  (the VSSDK build tooling otherwise filters it as a "VS platform assembly").

After this refactor these are **honestly scoped to the VS analytics adapter**, not a
Common-wide concern: they exist only because the VS host loads AppInsights in-process on
.NET Framework. The `net10.0` LSP server resolves `DiagnosticSource` via `deps.json` +
roll-forward and needs no redirect; VSCode (Node) and Rider (JVM) hosts are unaffected
entirely. If a future change removes in-process AppInsights from the VS host, both the
pkgdef and the target can be deleted.

| Risk | Impact | Mitigation |
|------|--------|------------|
| net46 AppInsights vs VS-forced DiagnosticSource 9.x | Was: package load failure | Binding-redirect pkgdef (registered as VsPackage asset) + DLL forced into VSIX |
| `TelemetryClient` disposal not managed by MEF | Low | `ReqnrollPluginPackage` holds the field and calls `DisposeAsync()` from `Dispose(bool)` via `JoinableTaskFactory.Run()` |
| Standard SDK API differs from VS SDK | Low | Near-identical at `TrackEvent`/`TrackException`; same namespace shape (see §2) |

---

## 7. Resolved design decisions

| Question | Decision |
|----------|----------|
| Where is telemetry transmitted? | **Host-side, per IDE.** Pre-LSP/lifecycle events (wizards, install) occur before the lazily-loaded server exists, so a server-side transmitter is impossible for them; routing in-session events through the same host sink is then strictly cheaper than a second server-side sink (see §0). |
| Does `Core/Common` depend on AppInsights? | **No.** `Common` holds only IDE-neutral contracts (`IAnalyticsTransmitter`, `IAnalyticsEvent`, events, `IEnableAnalyticsChecker`, `IUserUniqueIdStore`). Keeping AppInsights out of `Common` keeps it out of the `net10.0` LSP server's dependency graph (verified: no `Microsoft.ApplicationInsights.dll` in the server output). |
| Where does the concrete `TelemetryClient`-owning transmitter live? | **VSSDKIntegration**, as a single MEF-exported `AnalyticsTransmitter` (the former Core base class and VS subclass were merged). |
| How is it tested without a base/derived split? | An `internal` test-seam ctor (`InternalsVisibleTo` → `Reqnroll.VisualStudio.Tests`) that injects a `TelemetryClient` backed by an in-memory channel. |
| Where does `InstrumentationKey.txt` live? | **VSSDKIntegration**, embedded; resolved by resource-name suffix. (VSCode/Rider hosts will carry their own copy in their own languages.) |
| Which AppInsights version? | **v2.23.0** — `netstandard2.0`, minimal deps, no OpenTelemetry transitives. |
| Flush strategy? | SDK auto-flush (~30s) + explicit `Flush()` + 1s delay on `DisposeAsync()`. No per-event fire-and-forget. |
| Should analytics move to VS.Extensibility DI? | **No — stays in MEF.** Reqnroll Wizards cannot move to VS.Extensibility (it does not support the VSSDK wizard interface). Wizards consume `IMonitoringService` via MEF `[ImportingConstructor]`, and `MonitoringService` consumes `IAnalyticsTransmitter` the same way. Moving the transmitter to VS.E DI would break MEF composition for wizard callers; dual registration risks two `TelemetryClient` instances. |
| `ResolveMefService` bridge in `ReqnrollLanguageClient` | **Intentional — keep.** A VS.Extensibility component resolving a MEF service via `IComponentModel` is the standard adapter between the two composition systems, not a smell. |
| Does the server transmit telemetry? | **No.** `NullMonitoringService` is a no-op; `ILspTelemetryService.SendEvent` only emits `telemetry/event` notifications, which the VS `TelemetryEventInterceptor` forwards to the host transmitter. |
