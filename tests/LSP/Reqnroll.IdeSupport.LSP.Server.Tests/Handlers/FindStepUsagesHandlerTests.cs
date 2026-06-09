using AwesomeAssertions;
using NSubstitute;
using OmniSharp.Extensions.LanguageServer.Protocol;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using Reqnroll.IdeSupport.Common.Diagnostics;
using Reqnroll.IdeSupport.LSP.Core.Discovery;
using Reqnroll.IdeSupport.LSP.Core.Document;
using Reqnroll.IdeSupport.LSP.Core.Matching;
using Reqnroll.IdeSupport.LSP.Server.Discovery;
using Reqnroll.IdeSupport.LSP.Server.Document;
using Reqnroll.IdeSupport.LSP.Server.Handlers.ProtocolHandlers;
using Reqnroll.IdeSupport.LSP.Server.Workspace;

namespace Reqnroll.IdeSupport.LSP.Server.Tests.Handlers;

public class FindStepUsagesHandlerTests
{
    private readonly IBindingMatchService          _matchService   = Substitute.For<IBindingMatchService>();
    private readonly ILspWorkspaceScopeManager     _scopeManager   = Substitute.For<ILspWorkspaceScopeManager>();
    private readonly IProjectBindingRegistryLookup _registryLookup = Substitute.For<IProjectBindingRegistryLookup>();
    private readonly IDeveroomLogger               _logger         = Substitute.For<IDeveroomLogger>();

    private static readonly DocumentUri CsUri      = DocumentUri.FromFileSystemPath("/workspace/Steps.cs");
    private static readonly DocumentUri FeatureUri = DocumentUri.FromFileSystemPath("/workspace/test.feature");

    public FindStepUsagesHandlerTests()
    {
        _scopeManager.ResolveOwners(Arg.Any<DocumentUri>())
                     .Returns(Array.Empty<LspReqnrollProject>());
    }

    private FindStepUsagesHandler CreateSut() =>
        new(_matchService, _scopeManager, _registryLookup, _logger);

    private static ReferenceParams RequestAt(DocumentUri uri, int line, int character) =>
        new()
        {
            TextDocument = new TextDocumentIdentifier { Uri = uri },
            Position     = new Position(line, character),
            Context      = new ReferenceContext { IncludeDeclaration = false }
        };

    // Snapshot: "Feature: F\nScenario: S\n    Given a step\n"
    //   line 0 offset  0: "Feature: F\n"       (11 chars)
    //   line 1 offset 11: "Scenario: S\n"      (12 chars)
    //   line 2 offset 23: "    Given a step\n"
    //              offset 33: "a step"         (6 chars) — step expression, line 2 char 10
    private static StepBindingMatch MakeMatch(
        DocumentUri featureUri,
        int         startOffset,
        int         length,
        string?     keyword      = null,
        string?     scenarioName = null,
        string?     projectName  = null)
    {
        var snapshot = new LspTextSnapshot(featureUri.ToString(), 1,
            "Feature: F\nScenario: S\n    Given a step\n");
        var range = GherkinRange.FromPoint(snapshot, startOffset, length);
        return new StepBindingMatch(featureUri.ToString(), range, MatchResult.NoMatch,
                                    keyword, scenarioName, projectName);
    }

    // ── Non-.cs URI ───────────────────────────────────────────────────────────

    [Fact]
    public async Task Handle_non_cs_uri_returns_null_without_querying_match_service()
    {
        var result = await CreateSut().Handle(
            RequestAt(FeatureUri, 2, 0), CancellationToken.None);

        result.Should().BeNull();
        _matchService.DidNotReceive()
                     .FindUsages(Arg.Any<SourceLocation>(), Arg.Any<IReadOnlyCollection<ProjectOwner>>());
    }

    // ── Three-state: not a binding → {isBinding:false} ───────────────────────

    [Fact]
    public async Task Handle_caret_not_on_a_binding_returns_response_with_isBinding_false()
    {
        _matchService.FindUsages(Arg.Any<SourceLocation>(), Arg.Any<IReadOnlyCollection<ProjectOwner>>())
                     .Returns(Array.Empty<StepBindingMatch>());
        _registryLookup.HasBindingAtLocation(Arg.Any<DocumentUri>(), Arg.Any<SourceLocation>())
                        .Returns(false);

        var result = await CreateSut().Handle(
            RequestAt(CsUri, 0, 0), CancellationToken.None);

        // isBinding=false signals "not a binding": client falls through to built-in C# FAR.
        // (null is not used: OmniSharp's OnRequest framework sends an error response for null
        // returns from custom-method handlers instead of serialising JSON null.)
        result.Should().NotBeNull();
        result!.IsBinding.Should().BeFalse();
        result.Locations.Should().BeEmpty();
    }

    // ── Three-state: binding with 0 usages → {isBinding:true, locations:[]} ──

    [Fact]
    public async Task Handle_binding_with_no_usages_returns_response_with_isBinding_true_and_empty_locations()
    {
        _matchService.FindUsages(Arg.Any<SourceLocation>(), Arg.Any<IReadOnlyCollection<ProjectOwner>>())
                     .Returns(Array.Empty<StepBindingMatch>());
        _registryLookup.HasBindingAtLocation(Arg.Any<DocumentUri>(), Arg.Any<SourceLocation>())
                        .Returns(true);

        var result = await CreateSut().Handle(
            RequestAt(CsUri, 9, 0), CancellationToken.None);

        result.Should().NotBeNull();
        result!.IsBinding.Should().BeTrue();
        result.Locations.Should().BeEmpty();
    }

    // ── Three-state: binding with usages ─────────────────────────────────────

    [Fact]
    public async Task Handle_single_usage_returns_response_with_isBinding_true_and_one_location()
    {
        _matchService.FindUsages(Arg.Any<SourceLocation>(), Arg.Any<IReadOnlyCollection<ProjectOwner>>())
                     .Returns(new[] { MakeMatch(FeatureUri, 33, 6) });

        var result = await CreateSut().Handle(
            RequestAt(CsUri, 9, 0), CancellationToken.None);

        result.Should().NotBeNull();
        result!.IsBinding.Should().BeTrue();
        result.Locations.Should().ContainSingle();
    }

    [Fact]
    public async Task Handle_multiple_usages_returns_all_locations()
    {
        var secondUri = DocumentUri.FromFileSystemPath("/workspace/other.feature");
        _matchService.FindUsages(Arg.Any<SourceLocation>(), Arg.Any<IReadOnlyCollection<ProjectOwner>>())
                     .Returns(new[]
                     {
                         MakeMatch(FeatureUri, 33, 6),
                         MakeMatch(secondUri,  33, 6)
                     });

        var result = await CreateSut().Handle(
            RequestAt(CsUri, 9, 0), CancellationToken.None);

        result!.Locations.Should().HaveCount(2);
    }

    [Fact]
    public async Task Handle_binding_with_usages_does_not_call_HasBindingAtLocation()
    {
        _matchService.FindUsages(Arg.Any<SourceLocation>(), Arg.Any<IReadOnlyCollection<ProjectOwner>>())
                     .Returns(new[] { MakeMatch(FeatureUri, 33, 6) });

        await CreateSut().Handle(RequestAt(CsUri, 9, 0), CancellationToken.None);

        _registryLookup.DidNotReceive()
                        .HasBindingAtLocation(Arg.Any<DocumentUri>(), Arg.Any<SourceLocation>());
    }

    // ── Location fields ───────────────────────────────────────────────────────

    [Fact]
    public async Task Handle_location_uri_matches_feature_document_uri()
    {
        _matchService.FindUsages(Arg.Any<SourceLocation>(), Arg.Any<IReadOnlyCollection<ProjectOwner>>())
                     .Returns(new[] { MakeMatch(FeatureUri, 33, 6) });

        var result = await CreateSut().Handle(
            RequestAt(CsUri, 9, 0), CancellationToken.None);

        result!.Locations.Single().Uri.Should().Be(FeatureUri.ToString());
    }

    [Fact]
    public async Task Handle_location_coordinates_match_step_range_line_and_character()
    {
        _matchService.FindUsages(Arg.Any<SourceLocation>(), Arg.Any<IReadOnlyCollection<ProjectOwner>>())
                     .Returns(new[] { MakeMatch(FeatureUri, 33, 6) });

        var result = await CreateSut().Handle(
            RequestAt(CsUri, 9, 0), CancellationToken.None);

        // offset 33 in "Feature: F\n(11)Scenario: S\n(12)    Given a step\n"
        // → line 2, char 10; end char 16
        var loc = result!.Locations.Single();
        loc.StartLine.Should().Be(2);
        loc.StartChar.Should().Be(10);
        loc.EndLine.Should().Be(2);
        loc.EndChar.Should().Be(16);
    }

    [Fact]
    public async Task Handle_step_text_is_extracted_from_in_memory_snapshot()
    {
        _matchService.FindUsages(Arg.Any<SourceLocation>(), Arg.Any<IReadOnlyCollection<ProjectOwner>>())
                     .Returns(new[] { MakeMatch(FeatureUri, 33, 6) });

        var result = await CreateSut().Handle(
            RequestAt(CsUri, 9, 0), CancellationToken.None);

        // offset 33 length 6 in the snapshot → "a step" (trimmed)
        result!.Locations.Single().StepText.Should().Be("a step");
    }

    [Fact]
    public async Task Handle_keyword_scenario_name_and_project_name_are_propagated()
    {
        _matchService.FindUsages(Arg.Any<SourceLocation>(), Arg.Any<IReadOnlyCollection<ProjectOwner>>())
                     .Returns(new[]
                     {
                         MakeMatch(FeatureUri, 33, 6,
                                   keyword:      "Given",
                                   scenarioName: "Add numbers",
                                   projectName:  "MyProject")
                     });

        var result = await CreateSut().Handle(
            RequestAt(CsUri, 9, 0), CancellationToken.None);

        var loc = result!.Locations.Single();
        loc.Keyword.Should().Be("Given");
        loc.ScenarioName.Should().Be("Add numbers");
        loc.ProjectName.Should().Be("MyProject");
    }

    // ── Position conversion ───────────────────────────────────────────────────

    [Fact]
    public async Task Handle_lsp_line_is_converted_to_one_based_source_location_line()
    {
        SourceLocation? captured = null;
        _matchService.FindUsages(Arg.Do<SourceLocation>(loc => captured = loc),
                                 Arg.Any<IReadOnlyCollection<ProjectOwner>>())
                     .Returns(Array.Empty<StepBindingMatch>());
        _registryLookup.HasBindingAtLocation(Arg.Any<DocumentUri>(), Arg.Any<SourceLocation>())
                        .Returns(false);

        await CreateSut().Handle(RequestAt(CsUri, 9, 4), CancellationToken.None);

        captured!.SourceFileLine.Should().Be(10);
    }

    [Fact]
    public async Task Handle_lsp_character_is_converted_to_one_based_source_location_column()
    {
        SourceLocation? captured = null;
        _matchService.FindUsages(Arg.Do<SourceLocation>(loc => captured = loc),
                                 Arg.Any<IReadOnlyCollection<ProjectOwner>>())
                     .Returns(Array.Empty<StepBindingMatch>());
        _registryLookup.HasBindingAtLocation(Arg.Any<DocumentUri>(), Arg.Any<SourceLocation>())
                        .Returns(false);

        await CreateSut().Handle(RequestAt(CsUri, 9, 4), CancellationToken.None);

        captured!.SourceFileColumn.Should().Be(5);
    }

    // ── Project scoping ───────────────────────────────────────────────────────

    [Fact]
    public async Task Handle_passes_owner_filter_to_FindUsages_when_owners_are_known()
    {
        var ideScope = new LspIdeScope(Substitute.For<IDeveroomLogger>());
        var project  = new LspReqnrollProject(
            new Reqnroll.IdeSupport.LSP.Server.Protocol.ReqnrollProjectLoadedParams
            {
                WorkspaceFolder        = "/workspace",
                ProjectFile            = "/workspace/My.csproj",
                ProjectFolder          = "/workspace",
                OutputAssemblyPath     = "/workspace/bin/My.dll",
                TargetFrameworkMoniker = "net8.0"
            },
            ideScope);

        _scopeManager.ResolveOwners(CsUri).Returns(new[] { project });
        _matchService.FindUsages(Arg.Any<SourceLocation>(), Arg.Any<IReadOnlyCollection<ProjectOwner>>())
                     .Returns(Array.Empty<StepBindingMatch>());
        _registryLookup.HasBindingAtLocation(Arg.Any<DocumentUri>(), Arg.Any<SourceLocation>())
                        .Returns(false);

        await CreateSut().Handle(RequestAt(CsUri, 0, 0), CancellationToken.None);

        _matchService.Received(1).FindUsages(
            Arg.Any<SourceLocation>(),
            Arg.Is<IReadOnlyCollection<ProjectOwner>>(f =>
                f != null && f.Any(o => o.ProjectFile == project.ProjectFullName)));

        project.Dispose();
    }

    [Fact]
    public async Task Handle_passes_null_filter_to_FindUsages_when_no_owners_found()
    {
        _scopeManager.ResolveOwners(CsUri).Returns(Array.Empty<LspReqnrollProject>());
        _matchService.FindUsages(Arg.Any<SourceLocation>(), Arg.Any<IReadOnlyCollection<ProjectOwner>>())
                     .Returns(Array.Empty<StepBindingMatch>());
        _registryLookup.HasBindingAtLocation(Arg.Any<DocumentUri>(), Arg.Any<SourceLocation>())
                        .Returns(false);

        await CreateSut().Handle(RequestAt(CsUri, 0, 0), CancellationToken.None);

        _matchService.Received(1).FindUsages(
            Arg.Any<SourceLocation>(),
            Arg.Is<IReadOnlyCollection<ProjectOwner>?>(f => f == null));
    }
}
