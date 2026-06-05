using System.Collections.Immutable;
using Reqnroll.IdeSupport.Common.Diagnostics;
using Reqnroll.IdeSupport.Common.ProjectSystem;
using Reqnroll.IdeSupport.LSP.Core.Discovery;
using Reqnroll.IdeSupport.LSP.Server.Discovery;
using Reqnroll.IdeSupport.LSP.Server.Workspace;

namespace Reqnroll.IdeSupport.LSP.Server.Tests.Discovery;

public class ConnectorBindingRegistryProviderTests : IDisposable
{
    private readonly IDeveroomLogger _logger = Substitute.For<IDeveroomLogger>();
    private readonly IConnectorDiscoveryService _discovery = Substitute.For<IConnectorDiscoveryService>();
    private readonly LspIdeScope _ideScope;
    private readonly string _folder = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
    private readonly LspReqnrollProject _project;

    public ConnectorBindingRegistryProviderTests()
    {
        _ideScope = new LspIdeScope(_logger);
        _project = DiscoveryTestSupport.MakeProject(_ideScope, _folder);
    }

    public void Dispose() => _project.Dispose();

    private ConnectorBindingRegistryProvider CreateSut() => new(_project, _discovery, _logger);

    private static ProjectBindingRegistry NonInvalidRegistry(int hash) => new(
        ImmutableArray<ProjectStepDefinitionBinding>.Empty,
        ImmutableArray<ProjectHookBinding>.Empty,
        hash);

    private void GivenDiscoveryReturns(ProjectBindingRegistry registry, string hash)
        => _discovery.RunDiscovery(
                Arg.Any<IProjectScope>(),
                Arg.Any<ProjectBindingRegistry>(),
                Arg.Any<string>(),
                Arg.Any<CancellationToken>())
            .Returns((registry, hash));

    // ── Initial state ──────────────────────────────────────────────────────────

    [Fact]
    public void Current_is_invalid_before_any_discovery_runs()
    {
        CreateSut().Current.Should().BeSameAs(ProjectBindingRegistry.Invalid);
    }

    // ── Successful refresh ──────────────────────────────────────────────────────

    [Fact]
    public async Task TriggerRefresh_swaps_registry_and_raises_event_on_new_result()
    {
        var newRegistry = NonInvalidRegistry(hash: 42);
        GivenDiscoveryReturns(newRegistry, "hash-1");

        var sut = CreateSut();
        var changed = new TaskCompletionSource();
        sut.BindingRegistryChanged += (_, _) => changed.TrySetResult();

        sut.TriggerRefresh();

        var completed = await Task.WhenAny(changed.Task, Task.Delay(5000));
        completed.Should().BeSameAs(changed.Task, "discovery should complete and raise the change event");
        sut.Current.Should().BeSameAs(newRegistry);
    }

    // ── No-op refresh ────────────────────────────────────────────────────────────

    [Fact]
    public async Task TriggerRefresh_does_not_raise_event_when_hash_is_unchanged()
    {
        // Discovery returns the last-good registry with the same (empty) hash → no swap.
        GivenDiscoveryReturns(ProjectBindingRegistry.Invalid, string.Empty);

        var sut = CreateSut();
        var raised = false;
        sut.BindingRegistryChanged += (_, _) => raised = true;

        sut.TriggerRefresh();
        await Task.Delay(1200); // past the 500 ms debounce + run

        raised.Should().BeFalse();
        sut.Current.Should().BeSameAs(ProjectBindingRegistry.Invalid);
        _discovery.ReceivedWithAnyArgs().RunDiscovery(default!, default!, default!, default);
    }

    // ── Debounce: rapid triggers collapse to a single run ────────────────────────

    [Fact]
    public async Task TriggerRefresh_called_rapidly_cancels_earlier_runs()
    {
        var newRegistry = NonInvalidRegistry(hash: 7);
        GivenDiscoveryReturns(newRegistry, "hash-1");

        var sut = CreateSut();
        var changed = new TaskCompletionSource();
        sut.BindingRegistryChanged += (_, _) => changed.TrySetResult();

        // Three triggers inside the debounce window: only the last should survive to run.
        sut.TriggerRefresh();
        sut.TriggerRefresh();
        sut.TriggerRefresh();

        await Task.WhenAny(changed.Task, Task.Delay(5000));
        sut.Current.Should().BeSameAs(newRegistry);

        // The cancelled earlier runs never reach the discovery service; only one run executes.
        _discovery.ReceivedWithAnyArgs(1).RunDiscovery(default!, default!, default!, default);
    }

    // ── Roslyn source-level patch (F2) ───────────────────────────────────────────

    [Fact]
    public async Task ApplyRoslynFileUpdate_patches_current_registry_and_raises_event()
    {
        var sut = CreateSut();
        var changed = new TaskCompletionSource();
        sut.BindingRegistryChanged += (_, _) => changed.TrySetResult();

        var file = FileDetailsFor("Steps.cs", @"
namespace S
{
    [Reqnroll.Binding]
    public class Steps
    {
        [Reqnroll.Given(""the first number is (.*)"")]
        public void Method(int n) { }
    }
}");

        await sut.ApplyRoslynFileUpdateAsync(file);

        (await Task.WhenAny(changed.Task, Task.Delay(2000)))
            .Should().BeSameAs(changed.Task, "the source-level update should raise BindingRegistryChanged");
        sut.Current.Should().NotBeSameAs(ProjectBindingRegistry.Invalid);
        sut.Current.StepDefinitions.Should().ContainSingle()
            .Which.Regex!.ToString().Should().Be("^the first number is (.*)$");
    }

    [Fact]
    public async Task ApplyRoslynFileUpdate_replaces_only_that_files_bindings()
    {
        var sut = CreateSut();

        var first = FileDetailsFor("A.cs",
            "namespace S { [Reqnroll.Binding] class A { [Reqnroll.Given(\"a\")] void M(){} } }");
        var second = FileDetailsFor("B.cs",
            "namespace S { [Reqnroll.Binding] class B { [Reqnroll.Given(\"b\")] void M(){} } }");

        await sut.ApplyRoslynFileUpdateAsync(first);
        await sut.ApplyRoslynFileUpdateAsync(second);

        // Editing B.cs again must keep A.cs's binding and replace only B.cs's.
        var secondEdited = FileDetailsFor("B.cs",
            "namespace S { [Reqnroll.Binding] class B { [Reqnroll.Given(\"b2\")] void M(){} } }");
        await sut.ApplyRoslynFileUpdateAsync(secondEdited);

        sut.Current.StepDefinitions.Select(s => s.Expression)
            .Should().BeEquivalentTo(new[] { "a", "b2" });
    }

    private CSharpStepDefinitionFile FileDetailsFor(string fileName, string content) =>
        Reqnroll.IdeSupport.Common.FileDetails
            .FromPath(Path.Combine(_folder, fileName))
            .WithCSharpContent(content);

    // ── Dispose ──────────────────────────────────────────────────────────────────

    [Fact]
    public void Dispose_is_safe_to_call_without_any_refresh()
    {
        var sut = CreateSut();
        var act = () => sut.Dispose();
        act.Should().NotThrow();
    }

    [Fact]
    public async Task Dispose_cancels_a_pending_refresh_so_no_event_is_raised()
    {
        GivenDiscoveryReturns(NonInvalidRegistry(1), "hash-1");

        var sut = CreateSut();
        var raised = false;
        sut.BindingRegistryChanged += (_, _) => raised = true;

        sut.TriggerRefresh();      // schedules a run after the 500 ms debounce
        sut.Dispose();             // cancels it before the debounce elapses
        await Task.Delay(1000);

        raised.Should().BeFalse();
    }
}
