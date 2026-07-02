using Reqnroll.IdeSupport.Common.Configuration;
using Reqnroll.IdeSupport.Common.Diagnostics;
using Reqnroll.IdeSupport.Common.ProjectSystem;
using Reqnroll.IdeSupport.LSP.Connector.Models;
using Reqnroll.IdeSupport.LSP.Server.Discovery;
using Reqnroll.IdeSupport.LSP.Server.Workspace;

namespace Reqnroll.IdeSupport.LSP.Server.Tests.Discovery;

public class OutProcReqnrollConnectorTests
{
    /// <summary>
    /// Connector stub that points at a caller-supplied (non-existent) connector path so the
    /// base <see cref="OutProcReqnrollConnector.RunDiscovery"/> takes its missing-connector
    /// guard branch without spawning a process.  Named with the conventional suffix so
    /// <c>GetConnectorType()</c> resolves to "Fake".
    /// </summary>
    private sealed class FakeOutProcReqnrollConnector : OutProcReqnrollConnector
    {
        private readonly string _connectorPath;

        public FakeOutProcReqnrollConnector(string connectorPath)
            : base(
                new DeveroomConfiguration(),
                Substitute.For<IDeveroomLogger>(),
                TargetFrameworkMoniker.Create(".NETCoreApp,Version=v8.0"),
                AppContext.BaseDirectory,
                ProcessorArchitectureSetting.UseSystem,
                DiscoveryTestSupport.MinimalProjectSettings(
                    TargetFrameworkMoniker.Create(".NETCoreApp,Version=v8.0")),
                NullMonitoringService.Instance)
        {
            _connectorPath = connectorPath;
        }

        protected override string GetConnectorPath(List<string> arguments) => _connectorPath;
    }

    [Fact]
    public void RunDiscovery_returns_failed_result_when_connector_executable_is_missing()
    {
        var missingPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + "-no-connector.exe");
        var sut = new FakeOutProcReqnrollConnector(missingPath);

        var result = sut.RunDiscovery(
            testAssemblyPath: Path.Combine(Path.GetTempPath(), "SomeAssembly.dll"),
            configFilePath: string.Empty);

        result.IsFailed.Should().BeTrue();
        result.ErrorMessage.Should().Contain("Unable to find connector");
        result.ErrorMessage.Should().Contain(missingPath);
    }

    [Fact]
    public void RunDiscovery_stamps_the_connector_type_on_the_result()
    {
        var sut = new FakeOutProcReqnrollConnector(
            Path.Combine(Path.GetTempPath(), Guid.NewGuid() + "-no-connector.exe"));

        var result = sut.RunDiscovery(
            testAssemblyPath: Path.Combine(Path.GetTempPath(), "SomeAssembly.dll"),
            configFilePath: string.Empty);

        // GetConnectorType() strips the "OutProcReqnrollConnector" suffix from the type name.
        result.ConnectorType.Should().Be("Fake");
    }

    // ── Non-Windows dotnet-host resolution (see GetDotNetCommand) ──────────────
    // Exercised directly against the extracted pure function so both branches are
    // covered regardless of which OS actually runs the test.

    [Fact]
    public void ResolveNonWindowsDotNetCommand_falls_back_to_bare_dotnet_when_DOTNET_ROOT_is_null()
    {
        var command = OutProcReqnrollConnector.ResolveNonWindowsDotNetCommand(null);

        command.Should().Be("dotnet", "PATH resolution is the standard install on Linux/macOS");
    }

    [Fact]
    public void ResolveNonWindowsDotNetCommand_falls_back_to_bare_dotnet_when_DOTNET_ROOT_is_empty()
    {
        var command = OutProcReqnrollConnector.ResolveNonWindowsDotNetCommand("");

        command.Should().Be("dotnet", "PATH resolution is the standard install on Linux/macOS");
    }

    [Fact]
    public void ResolveNonWindowsDotNetCommand_prefers_DOTNET_ROOT_when_set()
    {
        var dotNetRoot = Path.Combine(Path.GetTempPath(), "dotnet-root");

        var command = OutProcReqnrollConnector.ResolveNonWindowsDotNetCommand(dotNetRoot);

        command.Should().Be(Path.Combine(dotNetRoot, "dotnet"));
    }
}
