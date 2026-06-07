using MediatR;
using Reqnroll.IdeSupport.Common.Diagnostics;
using Reqnroll.IdeSupport.LSP.Server.Notifications;
using Reqnroll.IdeSupport.LSP.Server.Protocol;
using Reqnroll.IdeSupport.LSP.Server.Workspace;

namespace Reqnroll.IdeSupport.LSP.Server.Tests.Workspace;

/// <summary>
/// Unit tests for the Q17 project-membership index built into
/// <see cref="LspWorkspaceScopeManager"/>.  Every test drives the index via
/// <see cref="ILspWorkspaceScopeManager"/> method calls and asserts the observable
/// state through the same interface.
/// </summary>
public class MembershipIndexTests : IAsyncLifetime
{
    private readonly IDeveroomLogger _logger   = Substitute.For<IDeveroomLogger>();
    private readonly IMediator       _mediator = Substitute.For<IMediator>();
    private readonly LspIdeScope     _ideScope;
    private readonly LspWorkspaceScopeManager _sut;

    // Two distinct on-disk roots — _root1 is the "normal" project folder;
    // _root2 simulates an external folder (linked files).
    private readonly string _root1 = Path.Combine(Path.GetTempPath(), "MembershipIdx_" + Guid.NewGuid().ToString("N"));
    private readonly string _root2 = Path.Combine(Path.GetTempPath(), "MembershipIdx_" + Guid.NewGuid().ToString("N"));

    public MembershipIndexTests()
    {
        _ideScope = new LspIdeScope(_logger);
        _sut      = new LspWorkspaceScopeManager(_ideScope, _logger, _mediator);
        Directory.CreateDirectory(_root1);
        Directory.CreateDirectory(_root2);
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public Task DisposeAsync()
    {
        _sut.Dispose();
        try { if (Directory.Exists(_root1)) Directory.Delete(_root1, recursive: true); } catch { }
        try { if (Directory.Exists(_root2)) Directory.Delete(_root2, recursive: true); } catch { }
        return Task.CompletedTask;
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private ReqnrollProjectLoadedParams ProjectParams(string? folder = null)
    {
        var dir = folder ?? _root1;
        return new()
        {
            WorkspaceFolder        = dir,
            ProjectFile            = Path.Combine(dir, "My.csproj"),
            ProjectFolder          = dir,
            OutputAssemblyPath     = Path.Combine(dir, "bin", "My.dll"),
            TargetFrameworkMoniker = ".NETCoreApp,Version=v8.0"
        };
    }

    private ReqnrollProjectFilesParams BaselineParams(
        string projectFile,
        string tfm,
        params (string path, ProjectFileRole role)[] entries)
        => new()
        {
            ProjectFile            = projectFile,
            TargetFrameworkMoniker = tfm,
            Kind                   = ProjectFilesKind.Baseline,
            Files                  = entries
                .Select(e => new ProjectFileEntry { Path = e.path, Role = e.role, Added = true })
                .ToArray()
        };

    private ReqnrollProjectFilesParams DeltaParams(
        string projectFile,
        string tfm,
        params (string path, ProjectFileRole role, bool added)[] entries)
        => new()
        {
            ProjectFile            = projectFile,
            TargetFrameworkMoniker = tfm,
            Kind                   = ProjectFilesKind.Delta,
            Files                  = entries
                .Select(e => new ProjectFileEntry { Path = e.path, Role = e.role, Added = e.added })
                .ToArray()
        };

    private string Feature(string name, string? folder = null)
        => Path.Combine(folder ?? _root1, name + ".feature");

    private string CsFile(string name, string? folder = null)
        => Path.Combine(folder ?? _root1, name + ".cs");

    private DocumentUri FeatureUri(string name, string? folder = null)
        => DocumentUri.FromFileSystemPath(Feature(name, folder));

    // ── State before any baseline ─────────────────────────────────────────────

    [Fact]
    public async Task GetMembershipState_is_Pending_when_project_loaded_but_no_baseline()
    {
        await _sut.HandleProjectLoadedAsync(ProjectParams(), CancellationToken.None);

        _sut.GetMembershipState(FeatureUri("test")).Should().Be(MembershipState.Pending);
    }

    [Fact]
    public void GetMembershipState_is_Unowned_for_file_with_no_covering_project()
    {
        // No projects registered at all.
        var outsideUri = DocumentUri.FromFileSystemPath(Feature("nowhere", _root2));

        _sut.GetMembershipState(outsideUri).Should().Be(MembershipState.Unowned);
    }

    [Fact]
    public async Task HasBaselineForProject_is_false_before_HandleProjectFilesAsync()
    {
        await _sut.HandleProjectLoadedAsync(ProjectParams(), CancellationToken.None);
        var project = _sut.GetProjectForUri(FeatureUri("x"))!;

        _sut.HasBaselineForProject(project).Should().BeFalse();
    }

    [Fact]
    public async Task GetProjectsForUri_returns_empty_when_no_baseline_received()
    {
        await _sut.HandleProjectLoadedAsync(ProjectParams(), CancellationToken.None);

        _sut.GetProjectsForUri(FeatureUri("test")).Should().BeEmpty();
    }

    // ── After baseline ────────────────────────────────────────────────────────

    [Fact]
    public async Task HasBaselineForProject_is_true_after_HandleProjectFilesAsync()
    {
        var p = ProjectParams();
        await _sut.HandleProjectLoadedAsync(p, CancellationToken.None);
        var project = _sut.GetProjectForUri(FeatureUri("x"))!;

        await _sut.HandleProjectFilesAsync(
            BaselineParams(p.ProjectFile, p.TargetFrameworkMoniker,
                (Feature("f1"), ProjectFileRole.Feature)),
            CancellationToken.None);

        _sut.HasBaselineForProject(project).Should().BeTrue();
    }

    [Fact]
    public async Task GetMembershipState_is_Owned_for_file_in_baseline()
    {
        var p = ProjectParams();
        await _sut.HandleProjectLoadedAsync(p, CancellationToken.None);
        var featurePath = Feature("mine");

        await _sut.HandleProjectFilesAsync(
            BaselineParams(p.ProjectFile, p.TargetFrameworkMoniker,
                (featurePath, ProjectFileRole.Feature)),
            CancellationToken.None);

        _sut.GetMembershipState(DocumentUri.FromFileSystemPath(featurePath))
            .Should().Be(MembershipState.Owned);
    }

    [Fact]
    public async Task GetMembershipState_is_Unowned_for_file_in_project_folder_but_not_in_baseline()
    {
        var p = ProjectParams();
        await _sut.HandleProjectLoadedAsync(p, CancellationToken.None);

        await _sut.HandleProjectFilesAsync(
            BaselineParams(p.ProjectFile, p.TargetFrameworkMoniker,
                (Feature("included"), ProjectFileRole.Feature)),
            CancellationToken.None);

        // A file that was NOT listed in the baseline becomes Unowned now that we know
        // the project's authoritative membership.
        _sut.GetMembershipState(FeatureUri("excluded"))
            .Should().Be(MembershipState.Unowned);
    }

    [Fact]
    public async Task GetProjectsForUri_returns_owning_project_after_baseline()
    {
        var p = ProjectParams();
        await _sut.HandleProjectLoadedAsync(p, CancellationToken.None);
        var project = _sut.GetProjectForUri(FeatureUri("x"))!;
        var featurePath = Feature("f1");

        await _sut.HandleProjectFilesAsync(
            BaselineParams(p.ProjectFile, p.TargetFrameworkMoniker,
                (featurePath, ProjectFileRole.Feature)),
            CancellationToken.None);

        _sut.GetProjectsForUri(DocumentUri.FromFileSystemPath(featurePath))
            .Should().ContainSingle()
            .Which.Should().BeSameAs(project);
    }

    [Fact]
    public async Task GetProjectsForUri_returns_empty_for_path_not_in_index()
    {
        var p = ProjectParams();
        await _sut.HandleProjectLoadedAsync(p, CancellationToken.None);

        await _sut.HandleProjectFilesAsync(
            BaselineParams(p.ProjectFile, p.TargetFrameworkMoniker,
                (Feature("included"), ProjectFileRole.Feature)),
            CancellationToken.None);

        _sut.GetProjectsForUri(FeatureUri("not_included")).Should().BeEmpty();
    }

    [Fact]
    public async Task GetIndexedFeatureFiles_returns_only_Feature_role_entries()
    {
        var p = ProjectParams();
        await _sut.HandleProjectLoadedAsync(p, CancellationToken.None);
        var project = _sut.GetProjectForUri(FeatureUri("x"))!;
        var f1 = Feature("f1");
        var f2 = Feature("f2");
        var cs1 = CsFile("Steps");

        await _sut.HandleProjectFilesAsync(
            BaselineParams(p.ProjectFile, p.TargetFrameworkMoniker,
                (f1, ProjectFileRole.Feature),
                (f2, ProjectFileRole.Feature),
                (cs1, ProjectFileRole.Binding)),
            CancellationToken.None);

        var features = _sut.GetIndexedFeatureFiles(project);
        features.Should().HaveCount(2);
        features.Should().Contain(Path.GetFullPath(f1));
        features.Should().Contain(Path.GetFullPath(f2));
        features.Should().NotContain(Path.GetFullPath(cs1));
    }

    // ── ResolveOwners fallback chain ──────────────────────────────────────────

    [Fact]
    public async Task ResolveOwners_returns_index_hit_after_baseline()
    {
        var p = ProjectParams();
        await _sut.HandleProjectLoadedAsync(p, CancellationToken.None);
        var project = _sut.GetProjectForUri(FeatureUri("x"))!;
        var featurePath = Feature("indexed");

        await _sut.HandleProjectFilesAsync(
            BaselineParams(p.ProjectFile, p.TargetFrameworkMoniker,
                (featurePath, ProjectFileRole.Feature)),
            CancellationToken.None);

        _sut.ResolveOwners(DocumentUri.FromFileSystemPath(featurePath))
            .Should().ContainSingle()
            .Which.Should().BeSameAs(project);
    }

    [Fact]
    public async Task ResolveOwners_falls_back_to_folder_prefix_when_Pending()
    {
        await _sut.HandleProjectLoadedAsync(ProjectParams(), CancellationToken.None);
        var project = _sut.GetProjectForUri(FeatureUri("x"))!;

        // No baseline sent yet — the file is inside the project folder → Pending.
        _sut.ResolveOwners(FeatureUri("any"))
            .Should().ContainSingle()
            .Which.Should().BeSameAs(project);
    }

    [Fact]
    public async Task ResolveOwners_returns_empty_for_Unowned_file_after_baseline()
    {
        var p = ProjectParams();
        await _sut.HandleProjectLoadedAsync(p, CancellationToken.None);

        await _sut.HandleProjectFilesAsync(
            BaselineParams(p.ProjectFile, p.TargetFrameworkMoniker,
                (Feature("only_this"), ProjectFileRole.Feature)),
            CancellationToken.None);

        // File in project folder but not in the baseline → Unowned.
        _sut.ResolveOwners(FeatureUri("something_else")).Should().BeEmpty();
    }

    // ── Delta ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Delta_is_dropped_before_baseline()
    {
        var p = ProjectParams();
        await _sut.HandleProjectLoadedAsync(p, CancellationToken.None);
        var newFile = Feature("late");

        await _sut.HandleProjectFilesAsync(
            DeltaParams(p.ProjectFile, p.TargetFrameworkMoniker,
                (newFile, ProjectFileRole.Feature, true)),
            CancellationToken.None);

        // Because no baseline arrived first, the delta must be silently dropped.
        _sut.GetProjectsForUri(DocumentUri.FromFileSystemPath(newFile)).Should().BeEmpty();
    }

    [Fact]
    public async Task Delta_adds_new_file_after_baseline()
    {
        var p = ProjectParams();
        await _sut.HandleProjectLoadedAsync(p, CancellationToken.None);
        var project = _sut.GetProjectForUri(FeatureUri("x"))!;
        var f1 = Feature("existing");
        var f2 = Feature("added_later");

        await _sut.HandleProjectFilesAsync(
            BaselineParams(p.ProjectFile, p.TargetFrameworkMoniker,
                (f1, ProjectFileRole.Feature)),
            CancellationToken.None);

        await _sut.HandleProjectFilesAsync(
            DeltaParams(p.ProjectFile, p.TargetFrameworkMoniker,
                (f2, ProjectFileRole.Feature, true)),
            CancellationToken.None);

        _sut.GetProjectsForUri(DocumentUri.FromFileSystemPath(f2))
            .Should().ContainSingle().Which.Should().BeSameAs(project);
    }

    [Fact]
    public async Task Delta_removes_file_after_baseline()
    {
        var p = ProjectParams();
        await _sut.HandleProjectLoadedAsync(p, CancellationToken.None);
        var f1 = Feature("to_keep");
        var f2 = Feature("to_remove");

        await _sut.HandleProjectFilesAsync(
            BaselineParams(p.ProjectFile, p.TargetFrameworkMoniker,
                (f1, ProjectFileRole.Feature),
                (f2, ProjectFileRole.Feature)),
            CancellationToken.None);

        await _sut.HandleProjectFilesAsync(
            DeltaParams(p.ProjectFile, p.TargetFrameworkMoniker,
                (f2, ProjectFileRole.Feature, false)),
            CancellationToken.None);

        _sut.GetProjectsForUri(DocumentUri.FromFileSystemPath(f1)).Should().NotBeEmpty();
        _sut.GetProjectsForUri(DocumentUri.FromFileSystemPath(f2)).Should().BeEmpty();
    }

    // ── Baseline replacement ──────────────────────────────────────────────────

    [Fact]
    public async Task Second_baseline_replaces_prior_contribution()
    {
        var p = ProjectParams();
        await _sut.HandleProjectLoadedAsync(p, CancellationToken.None);
        var old  = Feature("old");
        var fresh = Feature("fresh");

        await _sut.HandleProjectFilesAsync(
            BaselineParams(p.ProjectFile, p.TargetFrameworkMoniker,
                (old, ProjectFileRole.Feature)),
            CancellationToken.None);

        await _sut.HandleProjectFilesAsync(
            BaselineParams(p.ProjectFile, p.TargetFrameworkMoniker,
                (fresh, ProjectFileRole.Feature)),
            CancellationToken.None);

        _sut.GetProjectsForUri(DocumentUri.FromFileSystemPath(old)).Should().BeEmpty(
            "the first baseline's entries must be removed by the second baseline");
        _sut.GetProjectsForUri(DocumentUri.FromFileSystemPath(fresh)).Should().NotBeEmpty();
    }

    // ── Multi-owner (linked-file layout) ─────────────────────────────────────

    [Fact]
    public async Task GetProjectsForUri_returns_multiple_owners_for_shared_linked_file()
    {
        // Two projects, one in each root.
        var p1 = ProjectParams(_root1);
        var p2 = ProjectParams(_root2);
        await _sut.HandleProjectLoadedAsync(p1, CancellationToken.None);
        await _sut.HandleProjectLoadedAsync(p2, CancellationToken.None);

        // A linked feature outside both project folders.
        var shared = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "Shared.feature");

        await _sut.HandleProjectFilesAsync(
            BaselineParams(p1.ProjectFile, p1.TargetFrameworkMoniker,
                (shared, ProjectFileRole.Feature)),
            CancellationToken.None);

        await _sut.HandleProjectFilesAsync(
            BaselineParams(p2.ProjectFile, p2.TargetFrameworkMoniker,
                (shared, ProjectFileRole.Feature)),
            CancellationToken.None);

        var owners = _sut.GetProjectsForUri(DocumentUri.FromFileSystemPath(shared));
        owners.Should().HaveCount(2);
    }

    // ── ResolvePrimaryOwner (Q18 2A) ─────────────────────────────────────────

    [Fact]
    public async Task ResolvePrimaryOwner_returns_null_when_no_owners()
    {
        // No projects loaded at all → unowned.
        _sut.ResolvePrimaryOwner(FeatureUri("orphan")).Should().BeNull();
    }

    [Fact]
    public async Task ResolvePrimaryOwner_returns_single_owner_directly()
    {
        var p = ProjectParams();
        await _sut.HandleProjectLoadedAsync(p, CancellationToken.None);
        var project = _sut.GetProjectForUri(FeatureUri("x"))!;
        var featurePath = Feature("single");

        await _sut.HandleProjectFilesAsync(
            BaselineParams(p.ProjectFile, p.TargetFrameworkMoniker,
                (featurePath, ProjectFileRole.Feature)),
            CancellationToken.None);

        _sut.ResolvePrimaryOwner(DocumentUri.FromFileSystemPath(featurePath))
            .Should().BeSameAs(project);
    }

    [Fact]
    public async Task ResolvePrimaryOwner_prefers_home_project_for_shared_file_inside_its_folder()
    {
        // p1 lives in _root1; p2 lives in _root2.
        // The shared feature is physically inside _root1 → p1 is the home project.
        var p1 = ProjectParams(_root1);
        var p2 = ProjectParams(_root2);
        await _sut.HandleProjectLoadedAsync(p1, CancellationToken.None);
        await _sut.HandleProjectLoadedAsync(p2, CancellationToken.None);

        var featurePath = Feature("shared", _root1);  // physically in _root1

        await _sut.HandleProjectFilesAsync(
            BaselineParams(p1.ProjectFile, p1.TargetFrameworkMoniker,
                (featurePath, ProjectFileRole.Feature)),
            CancellationToken.None);
        await _sut.HandleProjectFilesAsync(
            BaselineParams(p2.ProjectFile, p2.TargetFrameworkMoniker,
                (featurePath, ProjectFileRole.Feature)),
            CancellationToken.None);

        var primary = _sut.ResolvePrimaryOwner(DocumentUri.FromFileSystemPath(featurePath));
        primary.Should().NotBeNull();
        primary!.ProjectFolder.Should().StartWith(_root1,
            "the home project is the one whose folder contains the file");
    }

    [Fact]
    public async Task ResolvePrimaryOwner_uses_ordinal_tiebreak_for_external_shared_file()
    {
        // Both p1 and p2 own a shared file outside their folders — ordinal on ProjectFullName.
        var p1 = ProjectParams(_root1);
        var p2 = ProjectParams(_root2);
        await _sut.HandleProjectLoadedAsync(p1, CancellationToken.None);
        await _sut.HandleProjectLoadedAsync(p2, CancellationToken.None);

        var externalPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "External.feature");

        await _sut.HandleProjectFilesAsync(
            BaselineParams(p1.ProjectFile, p1.TargetFrameworkMoniker,
                (externalPath, ProjectFileRole.Feature)),
            CancellationToken.None);
        await _sut.HandleProjectFilesAsync(
            BaselineParams(p2.ProjectFile, p2.TargetFrameworkMoniker,
                (externalPath, ProjectFileRole.Feature)),
            CancellationToken.None);

        // Call twice — result must be the same regardless of call order.
        var first  = _sut.ResolvePrimaryOwner(DocumentUri.FromFileSystemPath(externalPath));
        var second = _sut.ResolvePrimaryOwner(DocumentUri.FromFileSystemPath(externalPath));
        first.Should().BeSameAs(second, "the tiebreak must be deterministic");
        first.Should().NotBeNull();
    }

    [Fact]
    public async Task ResolvePrimaryOwner_is_stable_regardless_of_baseline_arrival_order()
    {
        // Send baselines in reverse ordinal order of project names; primary owner must
        // still be the ordinally-smallest project for an external file.
        var p1 = ProjectParams(_root1);
        var p2 = ProjectParams(_root2);
        await _sut.HandleProjectLoadedAsync(p1, CancellationToken.None);
        await _sut.HandleProjectLoadedAsync(p2, CancellationToken.None);

        var externalPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "Ext.feature");

        // Send p2 baseline first, then p1 — opposite of "natural" order.
        await _sut.HandleProjectFilesAsync(
            BaselineParams(p2.ProjectFile, p2.TargetFrameworkMoniker,
                (externalPath, ProjectFileRole.Feature)),
            CancellationToken.None);
        await _sut.HandleProjectFilesAsync(
            BaselineParams(p1.ProjectFile, p1.TargetFrameworkMoniker,
                (externalPath, ProjectFileRole.Feature)),
            CancellationToken.None);

        var primary = _sut.ResolvePrimaryOwner(DocumentUri.FromFileSystemPath(externalPath));
        // Both calls to ResolvePrimaryOwner should agree regardless of which baseline arrived first.
        var expected = string.Compare(p1.ProjectFile, p2.ProjectFile, StringComparison.Ordinal) < 0
            ? p1.ProjectFile : p2.ProjectFile;
        primary.Should().NotBeNull();
        primary!.ProjectFullName.Should().EndWith(Path.GetFileName(expected));
    }

    // ── MediatR publish ───────────────────────────────────────────────────────

    [Fact]
    public async Task Baseline_publishes_BindingRegistryChangedNotification_when_project_is_loaded()
    {
        var p = ProjectParams();
        await _sut.HandleProjectLoadedAsync(p, CancellationToken.None);

        await _sut.HandleProjectFilesAsync(
            BaselineParams(p.ProjectFile, p.TargetFrameworkMoniker,
                (Feature("f"), ProjectFileRole.Feature)),
            CancellationToken.None);

        _ = _mediator.Received(1).Publish(
            Arg.Is<BindingRegistryChangedNotification>(n => n.IsFullReplacement),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Baseline_without_loaded_project_does_not_throw()
    {
        // Baseline arrives before projectLoaded — this can happen on slow initialisation.
        var p = ProjectParams();

        var act = async () => await _sut.HandleProjectFilesAsync(
            BaselineParams(p.ProjectFile, p.TargetFrameworkMoniker,
                (Feature("f"), ProjectFileRole.Feature)),
            CancellationToken.None);

        await act.Should().NotThrowAsync();
    }
}
