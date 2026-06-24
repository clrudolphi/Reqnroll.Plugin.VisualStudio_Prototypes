using Reqnroll.IdeSupport.Common.Configuration;
using Reqnroll.IdeSupport.Common.Diagnostics;
using Reqnroll.IdeSupport.Common.ProjectSystem;
using Reqnroll.IdeSupport.LSP.Connector.Models;
using Reqnroll.IdeSupport.LSP.Core.Bindings;
using Reqnroll.IdeSupport.LSP.Server.Discovery;
using Reqnroll.IdeSupport.LSP.Server.Workspace;

namespace Reqnroll.IdeSupport.LSP.Server.Tests.Discovery;

public class ConnectorDiscoveryServiceTests : IDisposable
{
    private readonly IDeveroomLogger _logger = Substitute.For<IDeveroomLogger>();
    private readonly IOutProcConnectorFactory _factory = Substitute.For<IOutProcConnectorFactory>();
    private readonly string _projectFolder = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
    private readonly string _assemblyPath;

    public ConnectorDiscoveryServiceTests()
    {
        Directory.CreateDirectory(_projectFolder);
        _assemblyPath = Path.Combine(_projectFolder, "MyApp.Tests.dll");
        File.WriteAllText(_assemblyPath, "not a real assembly");
    }

    public void Dispose()
    {
        if (Directory.Exists(_projectFolder))
            Directory.Delete(_projectFolder, recursive: true);
    }

    // ── Fakes ─────────────────────────────────────────────────────────────────

    private sealed class FakeConnector : OutProcReqnrollConnector
    {
        private readonly DiscoveryResult _result;

        public FakeConnector(DiscoveryResult result)
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
            _result = result;
        }

        public override DiscoveryResult RunDiscovery(string testAssemblyPath, string configFilePath) => _result;
        protected override string GetConnectorPath(List<string> arguments) => "unused";
    }

    private IProjectScope MakeScope(string assemblyPath)
    {
        var scope = Substitute.For<IProjectScope>();
        scope.OutputAssemblyPath.Returns(assemblyPath);
        scope.ProjectName.Returns("MyApp.Tests");
        scope.ProjectFolder.Returns(_projectFolder);
        scope.TargetFrameworkMoniker.Returns(".NETCoreApp,Version=v8.0");
        return scope;
    }

    private void GivenConnectorReturns(DiscoveryResult result)
        => _factory.Create(Arg.Any<IProjectScope>()).Returns(new FakeConnector(result));

    private static DiscoveryResult SuccessfulResult() => new()
    {
        StepDefinitions =
        [
            new StepDefinition
            {
                Type           = "Given",
                Regex          = "^the first number is (.*)$",
                Method         = "MyApp.Steps.SetFirstNumber",
                ParamTypes     = "i",
                SourceLocation = "Steps.cs|10|5"
            }
        ],
        Hooks = []
    };

    private ConnectorDiscoveryService CreateSut() => new(_logger, _factory);

    // ── Happy path ─────────────────────────────────────────────────────────────

    [Fact]
    public void RunDiscovery_builds_registry_from_connector_result()
    {
        GivenConnectorReturns(SuccessfulResult());
        var scope = MakeScope(_assemblyPath);

        var (registry, hash) = CreateSut().RunDiscovery(
            scope, ProjectBindingRegistry.Invalid, lastHash: string.Empty, CancellationToken.None);

        registry.Should().NotBeSameAs(ProjectBindingRegistry.Invalid);
        registry.StepDefinitions.Should().HaveCount(1);
        hash.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public void RunDiscovery_invokes_the_factory_to_create_a_connector()
    {
        GivenConnectorReturns(SuccessfulResult());
        var scope = MakeScope(_assemblyPath);

        CreateSut().RunDiscovery(scope, ProjectBindingRegistry.Invalid, string.Empty, CancellationToken.None);

        _factory.Received(1).Create(scope);
    }

    // ── Hash guard ──────────────────────────────────────────────────────────────

    [Fact]
    public void RunDiscovery_skips_connector_when_hash_unchanged()
    {
        GivenConnectorReturns(SuccessfulResult());
        var scope = MakeScope(_assemblyPath);
        var sut = CreateSut();

        // First run computes the current hash.
        var (firstRegistry, hash) = sut.RunDiscovery(
            scope, ProjectBindingRegistry.Invalid, string.Empty, CancellationToken.None);

        _factory.ClearReceivedCalls();

        // Second run with the same hash must short-circuit and return the prior registry.
        var (secondRegistry, secondHash) = sut.RunDiscovery(
            scope, firstRegistry, hash, CancellationToken.None);

        secondRegistry.Should().BeSameAs(firstRegistry);
        secondHash.Should().Be(hash);
        _factory.DidNotReceive().Create(Arg.Any<IProjectScope>());
    }

    // ── Resilience ───────────────────────────────────────────────────────────────

    [Fact]
    public void RunDiscovery_returns_last_good_when_output_assembly_path_is_empty()
    {
        var scope = MakeScope(string.Empty);
        var lastGood = SuccessfulRegistry();

        var (registry, hash) = CreateSut().RunDiscovery(
            scope, lastGood, lastHash: "prev", CancellationToken.None);

        registry.Should().BeSameAs(lastGood);
        hash.Should().Be("prev");
        _factory.DidNotReceive().Create(Arg.Any<IProjectScope>());
    }

    [Fact]
    public void RunDiscovery_returns_last_good_when_output_assembly_missing_on_disk()
    {
        var scope = MakeScope(Path.Combine(_projectFolder, "does-not-exist.dll"));
        var lastGood = SuccessfulRegistry();

        var (registry, hash) = CreateSut().RunDiscovery(
            scope, lastGood, lastHash: "prev", CancellationToken.None);

        registry.Should().BeSameAs(lastGood);
        hash.Should().Be("prev");
        _factory.DidNotReceive().Create(Arg.Any<IProjectScope>());
    }

    [Fact]
    public void RunDiscovery_returns_last_good_when_connector_reports_failure()
    {
        GivenConnectorReturns(new DiscoveryResult { ErrorMessage = "boom" });
        var scope = MakeScope(_assemblyPath);
        var lastGood = SuccessfulRegistry();

        var (registry, hash) = CreateSut().RunDiscovery(
            scope, lastGood, lastHash: "prev", CancellationToken.None);

        registry.Should().BeSameAs(lastGood);
        hash.Should().Be("prev");
    }

    [Fact]
    public void RunDiscovery_returns_last_good_when_connector_throws()
    {
        _factory.Create(Arg.Any<IProjectScope>()).Returns(new ThrowingConnector());
        var scope = MakeScope(_assemblyPath);
        var lastGood = SuccessfulRegistry();

        var (registry, hash) = CreateSut().RunDiscovery(
            scope, lastGood, lastHash: "prev", CancellationToken.None);

        registry.Should().BeSameAs(lastGood);
        hash.Should().Be("prev");
    }

    private sealed class ThrowingConnector : OutProcReqnrollConnector
    {
        public ThrowingConnector()
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
        }

        public override DiscoveryResult RunDiscovery(string testAssemblyPath, string configFilePath)
            => throw new InvalidOperationException("connector blew up");

        protected override string GetConnectorPath(List<string> arguments) => "unused";
    }

    private ProjectBindingRegistry SuccessfulRegistry()
        => CreateSutWith(SuccessfulResult());

    private ProjectBindingRegistry CreateSutWith(DiscoveryResult result)
    {
        var localFactory = Substitute.For<IOutProcConnectorFactory>();
        localFactory.Create(Arg.Any<IProjectScope>()).Returns(new FakeConnector(result));
        var service = new ConnectorDiscoveryService(_logger, localFactory);
        var (registry, _) = service.RunDiscovery(
            MakeScope(_assemblyPath), ProjectBindingRegistry.Invalid, string.Empty, CancellationToken.None);
        return registry;
    }
}
