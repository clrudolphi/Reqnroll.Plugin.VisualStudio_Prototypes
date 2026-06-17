# Installation & Upgrade Wizard — Porting Assessment

## Overview

This document assesses what it would take to complete the port of the **Installation Wizard** (Welcome dialog — shown on first extension load) and the **Upgrade Wizard** (shown when the extension version changes) from the legacy `Reqnroll.VisualStudio` extension to the new `Reqnroll.IdeSupport` extension.

---

## Legacy Architecture (Reqnroll.VisualStudio)

### Flow Diagram

```
VS starts / solution opens
        │
        ▼
MonitoringService.MonitorOpenProjectSystem()
        │
        ▼
WelcomeService.OnIdeScopeActivityStarted(ideScope, monitoringService)
        │
        ├── Check ReqnrollInstallationStatus via RegistryManager (HKCU\Software\Reqnroll\VSLSP)
        │
        ├── If NOT installed (IsInstalled == false)
        │     ├── RegistryManager.UpdateStatus() — record install date/version
        │     ├── WindowsFileAssociationDetector.SetAssociation() — .feature → VS
        │     ├── MonitoringService.MonitorExtensionInstalled() — telemetry
        │     └── ScheduleWelcomeDialog(WelcomeDialogViewModel) — 7s delay
        │            └── DeveroomWindowManager.ShowDialog(viewModel)
        │                   └── Creates WelcomeDialog → DialogWindow.ShowModal()
        │
        ├── If installed AND version changed (InstalledVersion < current)
        │     ├── RegistryManager.UpdateStatus()
        │     ├── MonitoringService.MonitorExtensionUpgraded(version)
        │     ├── WindowsFileAssociationDetector.SetAssociation()
        │     ├── Read CHANGELOG.txt from extension folder
        │     └── ScheduleWelcomeDialog(UpgradeDialogViewModel) — 7s delay
        │            └── DeveroomWindowManager.ShowDialog(viewModel)
        │                   └── Creates WelcomeDialog → DialogWindow.ShowModal()
        │
        └── If installed AND same version (daily usage tracking)
              └── Update UsageDays / UserLevel in registry
```

### Key Files (Legacy)

| Layer | File | Purpose |
|-------|------|---------|
| **ViewModels** | `Reqnroll.VisualStudio/UI/ViewModels/WelcomeDialogViewModel.cs` | Page model with "Welcome", "Troubleshooting", "Community" markdown |
| | `Reqnroll.VisualStudio/UI/ViewModels/UpgradeDialogViewModel.cs` | "Changes" + "Community" pages, reads CHANGELOG |
| | `Reqnroll.VisualStudio/UI/ViewModels/WizardDialogs/WizardViewModel.cs` | Paged navigation base: Next/Previous commands |
| | `Reqnroll.VisualStudio/UI/ViewModels/WizardDialogs/WizardPageViewModel.cs` | Single page: Name, IsActive |
| | `Reqnroll.VisualStudio/UI/ViewModels/WizardDialogs/MarkDownWizardPageViewModel.cs` | Page with Text property |
| **UI** | `Reqnroll.VisualStudio.UI/Dialogs/WelcomeDialog.xaml` | WPF layout: Image, Pages, breadcrumbs, buttons |
| | `Reqnroll.VisualStudio.UI/Dialogs/WelcomeDialog.xaml.cs` | Code-behind: binds IVsUIShell → DialogWindow base |
| | `Reqnroll.VisualStudio.UI/DialogWindow.cs` | Base: VS-themed WPF window (IVsUIShell, native chrome) |
| | `Reqnroll.VisualStudio.UI/Controls/MarkDownTextBlock.cs` | WPF RichTextBox that renders basic markdown |
| | `Reqnroll.VisualStudio.UI/DeveroomResources.xaml` | Styles: VsDialogButton, VsDialogUpperPart, etc. |
| **Factory** | `Reqnroll.VisualStudio.UI/DeveroomWindowManager.cs` | IVsUIShell-based factory: creates dialogs, calls ShowModal |
| **Orchestrator** | `Reqnroll.VisualStudio/ProjectSystem/WelcomeService.cs` | Checks registry, schedules 7s-delayed dialog |
| **Registry** | `Reqnroll.VisualStudio/Analytics/RegistryManager.cs` | Reads/writes HKCU\Software\Reqnroll |
| | `Reqnroll.VisualStudio/Analytics/ReqnrollInstallationStatus.cs` | Status model: InstalledVersion, InstallDate, UsageDays |
| **Telemetry** | `Reqnroll.VisualStudio/Monitoring/MonitoringService.cs` | Tracks install, upgrade, dialog-dismissed events |
| | `Reqnroll.VisualStudio/Monitoring/IMonitoringService.cs` | Interface |
| **File Assoc** | `Reqnroll.VisualStudio/WindowsFileAssociationDetector.cs` | Registers .feature → Reqnroll.GherkinFile prog ID |
| **Notifications** | `Reqnroll.VisualStudio/Notifications/NotificationService.cs` | Optional HTTP poll for server notifications (DISABLED) |
| | `Reqnroll.VisualStudio/Notifications/NotificationInfoBar.cs` | IVsInfoBar UI (also disabled) |
| **Configuration** | `Reqnroll.VisualStudio/Analytics/GuidanceConfiguration.cs` | Usage-sequence triggers (2/5/10/20/100/200 days) |

---

## Current Porting State in Reqnroll.IdeSupport

### ✅ Already Ported

| File | Notes |
|------|-------|
| **WizardViewModel.cs** (Wizards.UI) | Ported; `DelegateCommand` replaced with `RelayCommand` |
| **WizardPageViewModel.cs** (Wizards.UI) | Ported verbatim |
| **MarkDownWizardPageViewModel.cs** (Wizards.UI) | Ported verbatim |
| **WelcomeDialogViewModel.cs** (Wizards.UI) | Ported verbatim (content strings) |
| **UpgradeDialogViewModel.cs** (Wizards.UI) | Ported verbatim (regex changelog parsing) |
| **WelcomeDialog.xaml** (Wizards.UI) | Ported; uses `WizardWindow` base class |
| **WelcomeDialog.xaml.cs** (Wizards.UI) | Ported; `IVsUIShell` dependency removed |
| **WizardWindow.cs** (Wizards.UI) | Ported from `DialogWindow`; VS SDK native-chrome removed; plain WPF `Window` |
| **MarkDownTextBlock.cs** (Wizards.UI) | Ported verbatim |
| **WizardResources.xaml** (Wizards.UI) | Ported from `DeveroomResources.xaml` |
| **VsWizardDialogService.cs** (Wizards) | `ShowWelcomeDialog()` **implemented**, `ShowUpgradeDialog()` **COMMENTED OUT** |
| **IWizardDialogService.cs** (Wizards.Core) | `ShowWelcomeDialog()` declared, `ShowUpgradeDialog()` **COMMENTED OUT** |
| **VsWizardTelemetry.cs** (Wizards) | Wizard telemetry adapter exists (for project/item templates) |
| **IWizardTelemetry.cs** (Wizards.Core) | Interface exists |
| **RegistryManager.cs** (VSSDKIntegration) | Ported; identical logic |
| **ReqnrollInstallationStatus.cs** (Common) | Ported to Common project |
| **IRegistryManager.cs** (Common) | Interface ported |
| **IMonitoringService.cs** (Common) | `MonitorWelcomeDialogDismissed`, `MonitorUpgradeDialogDismissed`, `MonitorExtensionInstalled`, `MonitorExtensionUpgraded` all declared |
| **MonitoringService.cs** (VSSDKIntegration) | Ported; all welcome/upgrade methods implemented; `_welcomeService` **COMMENTED OUT** |

### ❌ NOT Yet Ported

| Component | Priority | Notes |
|-----------|----------|-------|
| **UpgradeDialog.xaml** | **High** | Only WelcomeDialog.xaml exists. The upgrade dialog reuses the same layout but needs its own XAML (title/header differ). Could be the same XAML with different ViewModel binding. |
| **WelcomeService** (orchestrator) | **High** | The core logic that checks install status, schedules the dialog, creates VMs, calls the dialog service, and fires telemetry. The biggest missing piece. |
| **WindowsFileAssociationDetector** | **Medium** | .feature file association during first install. Registry-only, no VS SDK dependency — easy to port. |
| **Wiring: trigger point** | **High** | Where does `WelcomeService` get called? In the legacy, `MonitoringService.MonitorOpenProjectSystem` called `_welcomeService.OnIdeScopeActivityStarted`. In the new extension, `MonitoringService.MonitorOpenProjectSystem` has `_welcomeService` commented out. |
| **Link-clicked monitoring** | **Low** | `MonitorLinkClicked` is commented out in `IMonitoringService` and `MonitoringService`. Links in wizard dialogs work (browser opens) but aren't tracked. |
| **NotificationService** | **Very Low** | Was disabled in the legacy too (`//Task.Run(CheckAndNotifyAsync)` was commented out) |
| **IVersionProvider** (for extension version) | **Medium** | Used by `WelcomeService` to compare current vs installed version. Must be available for the status check. |

---

## Architecture Comparison

### Legacy: Generic Window Manager Pattern
```
ViewModel → IDeveroomWindowManager.ShowDialog<TViewModel>()
                → Type-dispatch → Specific Dialog (e.g. WelcomeDialog)
                → DialogWindow (IVsUIShell-based, VS-themed)
```

### New: Specific Dialog Service Pattern
```
ViewModel → IWizardDialogService.ShowWelcomeDialog()
                → Directly creates WelcomeDialog
                → WizardWindow (plain WPF, no VS SDK)
```

The new pattern is cleaner — no type-dispatch, no generic `TViewModel` constraint. Each dialog service method returns a specific result DTO or void.

---

## Porting Plan

### Phase 1: Core Orchestrator — WelcomeService (HIGH)

Create `IWelcomeService` + `WelcomeService` in the VSSDKIntegration project (mirroring the legacy, but using `IWizardDialogService` instead of `IDeveroomWindowManager`):

```csharp
public interface IWelcomeService
{
    void OnIdeScopeActivityStarted(IIdeScope ideScope, IMonitoringService monitoringService);
}
```

The service needs:
1. **IRegistryManager** (already ported) — check install status
2. **IVersionProvider** (already ported as `VersionProvider` in VSSDKIntegration) — get current extension version
3. **IWizardDialogService** (already ported, but needs `ShowUpgradeDialog` uncommented) — show dialogs
4. **IMonitoringService** (already ported) — fire telemetry
5. **IFileSystemForVS** — read CHANGELOG.txt (or similar)
6. **WindowsFileAssociationDetector** (needs porting) — set .feature association

**Timing:** Use a `DispatcherTimer` (or `Task.Delay` + `SwitchToMainThreadAsync`) for the 7-second delay before showing the dialog, as in the legacy.

### Phase 2: Uncomment ShowUpgradeDialog (HIGH)

Two files need the commented-out method uncommented:

1. **`IWizardDialogService.cs`** — uncomment `void ShowUpgradeDialog(string newVersion, string changeLog);`
2. **`VsWizardDialogService.cs`** — uncomment the method body, which creates `UpgradeDialog` + `UpgradeDialogViewModel`

This also requires creating `UpgradeDialog.xaml` and `UpgradeDialog.xaml.cs`.

### Phase 3: UpgradeDialog XAML (HIGH)

Create `UpgradeDialog.xaml` + `UpgradeDialog.xaml.cs` in Wizards.UI/Dialogs. Almost identical to WelcomeDialog — the XAML can be a near-copy (same `WizardWindow` base, same layout). The key difference is the ViewModel type (`UpgradeDialogViewModel` instead of `WelcomeDialogViewModel`) and the page content.

### Phase 4: WindowsFileAssociationDetector (MEDIUM)

Port `WindowsFileAssociationDetector.cs` to the VSSDKIntegration project. It uses `IFileSystemForVs` and `IIdeScope` (both available in the new extension). No VS SDK dependency beyond what already exists.

### Phase 5: Wire the Trigger (HIGH)

Decide where `WelcomeService.OnIdeScopeActivityStarted` should be called.

**Option A (legacy-parity):** Call it from `MonitoringService.MonitorOpenProjectSystem`. This requires uncommenting the `_welcomeService` dependency in `MonitoringService` and registering `IWelcomeService`/`WelcomeService` in MEF.

**Option B (new architecture):** Call it from `ReqnrollPluginPackage.InitializeAsync` after solution load completion. This avoids coupling MonitoringService to WelcomeService and keeps the startup path explicit. The package already has `WaitForSolutionLoadAsync` — the welcome/upgrade check can run there.

**Option C:** Call it from `ReqnrollLanguageClient.OnServerInitializationResultAsync` after the LSP server is ready. This ensures the user sees the dialog only when everything is initialized, but delays it unnecessarily.

**Recommendation:** Option A for parity (less refactoring), or Option B for cleaner architecture.

### Phase 6: Link Clicked Monitoring (LOW)

Uncomment `MonitorLinkClicked` in `IMonitoringService` and `MonitoringService`. The `WizardWindow` base class already fires `LinkClicked` events — the `VsWizardDialogService` can subscribe to these and call `_monitoringService.MonitorLinkClicked(...)`.

### Phase 7: CHANGELOG.txt availability (MEDIUM)

The legacy `WelcomeService` reads CHANGELOG.txt from `ideScope.GetExtensionFolder()`. The new extension needs to either:
- Embed CHANGELOG.txt as a VSIX content file
- Read it from the VSIX installation folder (accessible via `Path.GetDirectoryName(Assembly.Location)`)
- Pass it as a compiled resource

**Recommendation:** Include CHANGELOG.md as `Content` with `CopyToOutputDirectory=PreserveNewest` in the VSIX Extension project. The `WelcomeService` can find it relative to its own assembly location.

---

## Dependency Graph for Porting

```
                    ┌─────────────────────────┐
                    │     IVersionProvider     │  ← Already ported
                    └───────────┬─────────────┘
                                │
┌─────────────────┐    ┌───────▼────────────────┐
│  IRegistryManager ◄────┤                       │
│  (Common project) │    │     WelcomeService    │  ← NEW (biggest piece)
└─────────┬─────────┘    │    (orchestrator)     │
          │              └───┬───────────────────┘
          │                  │
          │                  ├──────────────────────────────┐
          │                  │                              │
          ▼                  ▼                              ▼
┌─────────────────┐  ┌──────────────┐        ┌──────────────────────┐
│ RegistryManager  │  │ IWizardDialog│        │   IMonitoringService  │
│ (VSSDKIntegration)│  │ Service    │        │   (Common project)    │
└─────────────────┘  └─────┬────────┘        └───┬──────────────────┘
                           │                     │
                           ▼                     ▼
                 ┌──────────────────┐   ┌─────────────────┐
                 │ VsWizardDialog    │   │  MonitoringService│
                 │ Service (Wizards) │   │ (VSSDKIntegration)│
                 └──┬───────────────┘   └─────────────────┘
                    │
                    ▼
          ┌─────────────────────────┐
          │ WelcomeDialog / Wizard  │
          │ Window (Wizards.UI)     │
          └─────────────────────────┘
```

---

## Clarifying Questions

Before implementing, some details need decisions:

1. **Trigger location (Phase 5):** Where should `WelcomeService.OnIdeScopeActivityStarted()` be called?
   - Option A: `MonitoringService.MonitorOpenProjectSystem` (legacy parity)
   - Option B: `ReqnrollPluginPackage.InitializeAsync` (cleaner decoupling)
   - Option C: `ReqnrollLanguageClient.OnServerInitializationResultAsync` (deferred)

2. **UpgradeDialog XAML:** Should `UpgradeDialog.xaml` be a separate file (duplicating the WelcomeDialog layout), or should the existing `WelcomeDialog` be refactored to accept either ViewModel type?
   - The legacy reused `WelcomeDialog` for both VMs (checked by `DeveroomWindowManager` — both `WelcomeDialogViewModel` and `UpgradeDialogViewModel` are routed to `WelcomeDialog`). The new extension could do the same since the XAML is identical.

3. **ChangeLog location:** Where should the CHANGELOG file live in the VSIX? Should it be an embedded resource, or a content file deployed alongside the extension?

4. **WindowsFileAssociationDetector:** Is .feature file association still desired for the new extension, or is this something to skip? The legacy registered `.feature` files with a `Reqnroll.GherkinFile` ProgID pointing to `devenv.exe /edit`.

5. **NotificationService:** The legacy had it disabled (`//Task.Run(CheckAndNotifyAsync)`). Should the NotificationService be ported at all, or left out of scope?

6. **Link-clicked telemetry:** The `WizardWindow` events are wired. Should `MonitorLinkClicked` be uncommented and wired in `VsWizardDialogService`, or can link tracking be considered low-value and dropped?

7. **GuidanceConfiguration:** The legacy has a usage-sequence concept (2/5/10/20/100/200 day milestones). This is purely telemetry and was mostly unused. Should it be ported?

8. **The `EnsureFeatureFileActivatedAsync` in `ReqnrollPluginPackage`:** When a first-run user opens VS, there are no `.feature` files to activate. Should the `WelcomeService` check run before or after the stub-frame initializer?
