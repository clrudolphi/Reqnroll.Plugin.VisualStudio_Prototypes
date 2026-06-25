#nullable enable

using System.Text.RegularExpressions;
using Gherkin;
using Gherkin.Ast;
using NSubstitute;
using OmniSharp.Extensions.LanguageServer.Protocol;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using Reqnroll.IdeSupport.Common.Configuration;
using Reqnroll.IdeSupport.Common.Diagnostics;
using Reqnroll.IdeSupport.Common.ProjectSystem.Configuration;
using Reqnroll.IdeSupport.LSP.Core.Bindings;


using Reqnroll.IdeSupport.LSP.Core.Documents;
using Reqnroll.IdeSupport.LSP.Core.Scaffolding;
using Reqnroll.IdeSupport.LSP.Core.Gherkin.Parsing;




using Reqnroll.IdeSupport.LSP.Core.Matching;
using Reqnroll.IdeSupport.LSP.Server.Document;
using Reqnroll.IdeSupport.LSP.Server.Features.CodeActions;
using Reqnroll.IdeSupport.LSP.Server.Workspace;
using AwesomeAssertions;
using Xunit;
using LspRange = OmniSharp.Extensions.LanguageServer.Protocol.Models.Range;

namespace Reqnroll.IdeSupport.LSP.Server.Tests.Handlers;

public class FeatureCodeActionHandlerTests
{
    private readonly BindingMatchService         _matchService  = new();
    private readonly IStepScaffoldService        _scaffoldService = new StepScaffoldService();
    private readonly ILspWorkspaceScopeManager   _scopeManager  = Substitute.For<ILspWorkspaceScopeManager>();
    private readonly IDeveroomLogger             _logger        = Substitute.For<IDeveroomLogger>();
    private readonly IDeveroomConfigurationProvider _configProvider = Substitute.For<IDeveroomConfigurationProvider>();

    private const string FeatureText =
        "Feature: F\nScenario: S\n    Given a step\n    When I press add\n";

    private static readonly DocumentUri FeatureUri =
        DocumentUri.FromFileSystemPath("/workspace/test.feature");

    public FeatureCodeActionHandlerTests()
    {
        _scopeManager.ResolvePrimaryOwner(Arg.Any<DocumentUri>())
                     .Returns((LspReqnrollProject?)null);

        _scopeManager.GetConfigurationProviderForUri(Arg.Any<DocumentUri>())
                     .Returns(_configProvider);

        _configProvider.GetConfiguration()
                       .Returns(new DeveroomConfiguration());
    }

    private FeatureCodeActionHandler CreateSut() =>
        new(_matchService, _scaffoldService, _scopeManager, _logger);

    private static CodeActionParams RequestAt(DocumentUri uri, int line = 0) =>
        new()
        {
            TextDocument = new TextDocumentIdentifier { Uri = uri },
            Range = new LspRange(new Position(line, 0), new Position(line, 0)),
            Context = new CodeActionContext { Diagnostics = new Container<Diagnostic>() }
        };

    // ── Guard rails ───────────────────────────────────────────────────────────

    [Fact]
    public async Task Returns_null_for_non_feature_URI()
    {
        var csUri = DocumentUri.FromFileSystemPath("/workspace/Steps.cs");
        var sut   = CreateSut();

        var result = await sut.Handle(RequestAt(csUri), CancellationToken.None);

        result.Should().BeNull();
    }

    [Fact]
    public async Task Returns_null_when_no_undefined_steps()
    {
        // No match set registered → all steps appear undefined but the cache returns empty
        var sut    = CreateSut();
        var result = await sut.Handle(RequestAt(FeatureUri), CancellationToken.None);

        // Nothing in the match service → no actions
        result.Should().BeNull();
    }

    // ── With undefined steps ──────────────────────────────────────────────────

    [Fact]
    public async Task Returns_define_all_action_for_single_undefined_step()
    {
        SeedMatchService(UndefinedMatch("I press add", ScenarioBlock.When));

        var result = await CreateSut().Handle(RequestAt(FeatureUri), CancellationToken.None);

        result.Should().NotBeNull();
        var actions = result!.ToList();
        actions.Should().HaveCount(1);
        var action = actions[0].CodeAction!;
        action.Title.Should().Be("Define missing step");
        action.Kind.Should().Be(CodeActionKind.QuickFix);
    }

    [Fact]
    public async Task Returns_define_all_action_for_multiple_undefined_steps()
    {
        SeedMatchService(
            UndefinedMatch("I press add",     ScenarioBlock.When),
            UndefinedMatch("the result is 4", ScenarioBlock.Then));

        var result = await CreateSut().Handle(RequestAt(FeatureUri), CancellationToken.None);

        result.Should().NotBeNull();
        var actions = result!.ToList();
        // Should include at minimum "Define all missing steps in file"
        actions.Should().HaveCountGreaterThanOrEqualTo(1);
        actions.Should().Contain(a =>
            a.CodeAction != null &&
            a.CodeAction.Title.Contains("Define all missing steps"));
    }

    [Fact]
    public async Task WorkspaceEdit_targets_new_cs_file_alongside_feature()
    {
        SeedMatchService(UndefinedMatch("I press add", ScenarioBlock.When));

        var result = await CreateSut().Handle(RequestAt(FeatureUri), CancellationToken.None);

        var action = result!.First().CodeAction!;
        action.Edit.Should().NotBeNull();
        // The edit must reference a .cs file in the same folder as the feature
        var edits = action.Edit!.DocumentChanges!.ToList();
        edits.Should().NotBeEmpty();
        var textEdit = edits.FirstOrDefault(e => e.IsTextDocumentEdit);
        textEdit.Should().NotBeNull();
        textEdit!.TextDocumentEdit!.TextDocument.Uri.Path
            .Should().EndWith(".cs");
    }

    [Fact]
    public async Task Generated_file_content_contains_step_expression()
    {
        SeedMatchService(UndefinedMatch("I press add", ScenarioBlock.When));

        var result = await CreateSut().Handle(RequestAt(FeatureUri), CancellationToken.None);

        var action = result!.First().CodeAction!;
        var edits  = action.Edit!.DocumentChanges!.ToList();
        var textEdit = edits.First(e => e.IsTextDocumentEdit);
        var newText = textEdit.TextDocumentEdit!.Edits.First().NewText;

        newText.Should().Contain("WhenIPressAdd");
        newText.Should().Contain("[Binding]");
        newText.Should().Contain("throw new PendingStepException();");
    }

    [Fact]
    public async Task Deduplicates_identical_step_expressions()
    {
        // Two undefined matches with exactly the same step text → one stub
        SeedMatchService(
            UndefinedMatch("I press add", ScenarioBlock.When),
            UndefinedMatch("I press add", ScenarioBlock.When));

        var result = await CreateSut().Handle(RequestAt(FeatureUri), CancellationToken.None);

        var action   = result!.First().CodeAction!;
        var textEdit = action.Edit!.DocumentChanges!
            .First(e => e.IsTextDocumentEdit)
            .TextDocumentEdit!.Edits.First().NewText;

        var occurrences = System.Text.RegularExpressions.Regex
            .Matches(textEdit, @"WhenIPressAdd").Count;
        occurrences.Should().Be(1);
    }

    [Fact]
    public async Task Uses_numeric_suffix_when_target_file_already_exists()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(tempDir);
        try
        {
            var featurePath  = Path.Combine(tempDir, "calculator.feature");
            var featureUri   = DocumentUri.FromFileSystemPath(featurePath);
            var className    = StepDefinitionFileBuilder.ClassNameFromFeaturePath(featurePath);

            // Pre-create the file the handler would normally target
            File.WriteAllText(Path.Combine(tempDir, className + ".cs"), "// existing");

            _scopeManager.GetConfigurationProviderForUri(featureUri).Returns(_configProvider);
            _scopeManager.ResolvePrimaryOwner(featureUri).Returns((LspReqnrollProject?)null);
            SeedMatchServiceFor(featureUri, UndefinedMatch("I press add", ScenarioBlock.When, featureUri));

            var result = await CreateSut().Handle(RequestAt(featureUri), CancellationToken.None);

            var action  = result!.First().CodeAction!;
            var changes = action.Edit!.DocumentChanges!.ToList();

            var createOp  = changes.First(e => e.IsCreateFile).CreateFile!;
            var textDocOp = changes.First(e => e.IsTextDocumentEdit).TextDocumentEdit!;

            // Both operations must target the suffixed file, not the pre-existing one
            createOp.Uri.Path.Should().EndWith(className + "2.cs");
            textDocOp.TextDocument.Uri.Path.Should().EndWith(className + "2.cs");

            // The generated class name must match the file name to avoid a duplicate-class conflict
            var generatedText = textDocOp.Edits.First().NewText;
            generatedText.Should().Contain($"class {className}2");
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public async Task Increments_suffix_until_free_name_is_found()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(tempDir);
        try
        {
            var featurePath = Path.Combine(tempDir, "calculator.feature");
            var featureUri  = DocumentUri.FromFileSystemPath(featurePath);
            var className   = StepDefinitionFileBuilder.ClassNameFromFeaturePath(featurePath);

            // Pre-create both the base name and the "2" variant
            File.WriteAllText(Path.Combine(tempDir, className + ".cs"),  "// existing");
            File.WriteAllText(Path.Combine(tempDir, className + "2.cs"), "// existing 2");

            _scopeManager.GetConfigurationProviderForUri(featureUri).Returns(_configProvider);
            _scopeManager.ResolvePrimaryOwner(featureUri).Returns((LspReqnrollProject?)null);
            SeedMatchServiceFor(featureUri, UndefinedMatch("I press add", ScenarioBlock.When, featureUri));

            var result = await CreateSut().Handle(RequestAt(featureUri), CancellationToken.None);

            var createOp = result!.First().CodeAction!
                .Edit!.DocumentChanges!.First(e => e.IsCreateFile).CreateFile!;

            createOp.Uri.Path.Should().EndWith(className + "3.cs");
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private void SeedMatchService(params StepBindingMatch[] matches) =>
        SeedMatchServiceFor(FeatureUri, matches);

    private void SeedMatchServiceFor(DocumentUri uri, params StepBindingMatch[] matches)
    {
        var matchSet = new FeatureBindingMatchSet(
            uri.ToString(),
            ProjectOwner.Unknown,
            documentVersion: 1,
            registryVersion: 1,
            steps: matches);

        _matchService.Store(matchSet);
    }

    private static StepBindingMatch UndefinedMatch(string text, ScenarioBlock block) =>
        UndefinedMatch(text, block, FeatureUri);

    private static StepBindingMatch UndefinedMatch(string text, ScenarioBlock block, DocumentUri uri)
    {
        var keyword = block switch
        {
            ScenarioBlock.Given => "Given ",
            ScenarioBlock.When  => "When ",
            _                   => "Then "
        };
        var gherkinStep = new DeveroomGherkinStep(
            new Gherkin.Ast.Location(0, 0), keyword, StepKeywordType.Context, text, null!,
            StepKeyword.Given, block);

        var item = MatchResultItem.CreateUndefined(gherkinStep, text);
        var result = MatchResult.CreateMultiMatch(new[] { item });

        var snapshot = new LspTextSnapshot(uri.ToString(), 1, FeatureText);
        var range    = GherkinRange.FromPoint(snapshot, 0, text.Length);

        return new StepBindingMatch(uri.ToString(), range, result,
            keyword.Trim(), "S", null);
    }
}
