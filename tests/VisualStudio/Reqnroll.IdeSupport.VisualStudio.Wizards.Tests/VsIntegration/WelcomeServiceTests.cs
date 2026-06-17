using NSubstitute;
using Reqnroll.IdeSupport.Common.Analytics;
using System.Text.RegularExpressions;

namespace Reqnroll.IdeSupport.VisualStudio.Wizards.Tests.VsIntegration;

public class WelcomeServiceTests
{
    private readonly IRegistryManager _registryManager = Substitute.For<IRegistryManager>();
    private readonly IVersionProvider _versionProvider = Substitute.For<IVersionProvider>();
    private readonly IWizardDialogService _dialogService = Substitute.For<IWizardDialogService>();
    private readonly IFileSystemForIDE _fileSystem = Substitute.For<IFileSystemForIDE>();
    private readonly IIdeScope _ideScope = Substitute.For<IIdeScope>();
    private readonly IMonitoringService _monitoringService = Substitute.For<IMonitoringService>();

    public WelcomeServiceTests()
    {
        _versionProvider.GetExtensionVersion().Returns("1.0.0");
        _ideScope.MonitoringService.Returns(_monitoringService);
        _ideScope.FileSystem.Returns(_fileSystem);
    }

    private TestableWelcomeService CreateService() =>
        new(_registryManager, _versionProvider, _dialogService, _fileSystem);

    // ── First install ────────────────────────────────────────────────

    [Fact]
    public void First_install_shows_welcome_dialog()
    {
        _registryManager.GetInstallStatus().Returns(new ReqnrollInstallationStatus());

        CreateService().OnIdeScopeActivityStarted(_ideScope);

        _dialogService.Received(1).ShowWelcomeDialog();
    }

    [Fact]
    public void First_install_fires_installed_telemetry()
    {
        _registryManager.GetInstallStatus().Returns(new ReqnrollInstallationStatus());

        CreateService().OnIdeScopeActivityStarted(_ideScope);

        _monitoringService.Received(1).MonitorExtensionInstalled();
    }

    [Fact]
    public void First_install_updates_registry()
    {
        var status = new ReqnrollInstallationStatus();
        _registryManager.GetInstallStatus().Returns(status);

        CreateService().OnIdeScopeActivityStarted(_ideScope);

        _registryManager.Received(1).UpdateStatus(Arg.Is<ReqnrollInstallationStatus>(s =>
            s.IsInstalled && s.InstalledVersion == new Version(1, 0, 0)));
    }

    [Fact]
    public void First_install_fires_welcome_dismissed_telemetry()
    {
        _registryManager.GetInstallStatus().Returns(new ReqnrollInstallationStatus());

        CreateService().OnIdeScopeActivityStarted(_ideScope);

        _monitoringService.Received(1).MonitorWelcomeDialogDismissed(
            Arg.Is<Dictionary<string, object>>(d =>
                d["ExtensionVersion"]!.ToString() == "1.0.0"));
    }

    // ── Version upgrade ──────────────────────────────────────────────

    private static ReqnrollInstallationStatus UpgradableStatus =>
        new()
        {
            InstalledVersion = new Version(0, 9, 0),
            InstallDate = DateTime.Today,
            LastUsedDate = DateTime.Today,
        };

    [Fact]
    public void Version_upgrade_shows_upgrade_dialog()
    {
        _registryManager.GetInstallStatus().Returns(UpgradableStatus);

        CreateService().OnIdeScopeActivityStarted(_ideScope);

        _dialogService.Received(1).ShowUpgradeDialog(
            Arg.Any<string>(), Arg.Any<string>());
    }

    [Fact]
    public void Version_upgrade_fires_upgraded_telemetry()
    {
        _registryManager.GetInstallStatus().Returns(UpgradableStatus);

        CreateService().OnIdeScopeActivityStarted(_ideScope);

        _monitoringService.Received(1).MonitorExtensionUpgraded("0.9.0");
    }

    [Fact]
    public void Version_upgrade_fires_upgrade_dismissed_telemetry()
    {
        _registryManager.GetInstallStatus().Returns(UpgradableStatus);

        CreateService().OnIdeScopeActivityStarted(_ideScope);

        _monitoringService.Received(1).MonitorUpgradeDialogDismissed(
            Arg.Is<Dictionary<string, object>>(d =>
                d["OldExtensionVersion"]!.ToString() == "0.9.0" &&
                d["NewExtensionVersion"]!.ToString() == "1.0.0"));
    }

    // ── Same version (no change) ─────────────────────────────────────

    [Fact]
    public void Same_version_shows_no_dialog()
    {
        _registryManager.GetInstallStatus().Returns(new ReqnrollInstallationStatus
        {
            InstalledVersion = new Version(1, 0, 0),
            InstallDate = DateTime.Today,
            LastUsedDate = DateTime.Today,
        });

        CreateService().OnIdeScopeActivityStarted(_ideScope);

        _dialogService.DidNotReceiveWithAnyArgs().ShowWelcomeDialog();
        _dialogService.DidNotReceiveWithAnyArgs().ShowUpgradeDialog(default!, default!);
    }

    // ── Daily usage tracking ─────────────────────────────────────────

    [Fact]
    public void Existing_user_new_day_updates_usage_days()
    {
        _registryManager.GetInstallStatus().Returns(new ReqnrollInstallationStatus
        {
            InstalledVersion = new Version(1, 0, 0),
            InstallDate = DateTime.Today,
            LastUsedDate = DateTime.Today.AddDays(-1),
            UsageDays = 5,
        });

        CreateService().OnIdeScopeActivityStarted(_ideScope);

        _registryManager.Received(1).UpdateStatus(Arg.Is<ReqnrollInstallationStatus>(s =>
            s.UsageDays == 6 && s.LastUsedDate == DateTime.Today));
    }

    // ── ChangeLog selection ──────────────────────────────────────────

    [Fact]
    public void Upgrade_passess_changelog_to_dialog()
    {
        _registryManager.GetInstallStatus().Returns(UpgradableStatus);

        var service = CreateService();
        service.StubbedChangeLog = "# v1.0.0\nNew stuff\n# v0.9.0\nOld stuff";
        service.OnIdeScopeActivityStarted(_ideScope);

        _dialogService.Received(1).ShowUpgradeDialog("1.0.0",
            Arg.Is<string>(s => s.Contains("New stuff") && !s.Contains("Old stuff")));
    }

    [Fact]
    public void Upgrade_with_empty_changelog_passes_empty_string()
    {
        _registryManager.GetInstallStatus().Returns(UpgradableStatus);

        var service = CreateService();
        service.StubbedChangeLog = "";
        service.OnIdeScopeActivityStarted(_ideScope);

        _dialogService.Received(1).ShowUpgradeDialog("1.0.0", "");
    }

    // ── Testable subclass ────────────────────────────────────────────

    private sealed class TestableWelcomeService : WelcomeService
    {
        public string? StubbedChangeLog { get; set; }

        public TestableWelcomeService(
            IRegistryManager registryManager,
            IVersionProvider versionProvider,
            IWizardDialogService dialogService,
            IFileSystemForIDE fileSystem)
            : base(registryManager, versionProvider, dialogService, fileSystem)
        {
        }

        /// <summary>Run the scheduled action immediately instead of after 7s.</summary>
        protected override void ScheduleAndShow(Action showAction) => showAction();

        /// <summary>No-op: avoid real registry writes in tests.</summary>
        protected override void CheckFileAssociation(IIdeScope ideScope) { }

        /// <summary>Use stubbed changelog to test selection logic.</summary>
        protected override string GetSelectedChangeLog(string oldExtensionVersion)
        {
            if (StubbedChangeLog is null)
                return base.GetSelectedChangeLog(oldExtensionVersion);

            int start = 0;
            var newVersionMatch = Regex.Match(StubbedChangeLog,
                @"^# v1.0.0", RegexOptions.Multiline);
            if (newVersionMatch.Success)
                start = newVersionMatch.Index;

            int end = StubbedChangeLog.Length;
            if (oldExtensionVersion != null)
            {
                var oldVersionMatch = Regex.Match(StubbedChangeLog,
                    @"^# v" + oldExtensionVersion, RegexOptions.Multiline);
                if (oldVersionMatch.Success)
                    end = oldVersionMatch.Index;
            }

            if (end <= start) return string.Empty;
            return StubbedChangeLog.Substring(start, end - start);
        }
    }
}
