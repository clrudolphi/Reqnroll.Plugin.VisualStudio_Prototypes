using System.Text.RegularExpressions;
using OmniSharp.Extensions.LanguageServer.Protocol;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using Reqnroll.IdeSupport.Common.Diagnostics;
using Reqnroll.IdeSupport.LSP.Core.Bindings;
using Reqnroll.IdeSupport.LSP.Core.Documents;

using Reqnroll.IdeSupport.LSP.Core.Discovery;
using Reqnroll.IdeSupport.LSP.Core.Document;

using Reqnroll.IdeSupport.LSP.Core.Gherkin.Parsing;
using Reqnroll.IdeSupport.LSP.Core.Matching;
using Reqnroll.IdeSupport.LSP.Server.Discovery;
using Reqnroll.IdeSupport.LSP.Server.Document;
using Reqnroll.IdeSupport.LSP.Server.Features.Rename;
using Reqnroll.IdeSupport.LSP.Server.Handlers.ProtocolHandlers;
using Reqnroll.IdeSupport.LSP.Server.Protocol;
using Reqnroll.IdeSupport.LSP.Server.Features.TextSync;
using Reqnroll.IdeSupport.LSP.Server.Services;
using Reqnroll.IdeSupport.LSP.Server.Workspace;

namespace Reqnroll.IdeSupport.LSP.Server.Tests.Handlers;

public class StepRenameHandlerTests
{
    private readonly IBindingMatchService          _matchService   = Substitute.For<IBindingMatchService>();
    private readonly ILspWorkspaceScopeManager     _scopeManager   = Substitute.For<ILspWorkspaceScopeManager>();
    private readonly IProjectBindingRegistryLookup _registryLookup = Substitute.For<IProjectBindingRegistryLookup>();
    private readonly IDeveroomLogger               _logger         = Substitute.For<IDeveroomLogger>();
    private readonly IDocumentBufferService         _documentBuffer = Substitute.For<IDocumentBufferService>();

    private static readonly DocumentUri CsUri = DocumentUri.FromFileSystemPath("/workspace/Steps.cs");
    private static string CsPath => CsUri.GetFileSystemPath();

    public StepRenameHandlerTests()
    {
        // No project owners → FindUsages is called with a null filter and there are
        // no feature-side edits; the tests focus on the generated C# attribute edit.
        _scopeManager.ResolveOwners(Arg.Any<DocumentUri>())
                     .Returns(Array.Empty<LspReqnrollProject>());
        _matchService.FindUsages(Arg.Any<SourceLocation>(), Arg.Any<IReadOnlyCollection<ProjectOwner>>())
                     .Returns(System.Array.Empty<StepBindingMatch>());
    }

    private StepRenameHandler CreateSut() =>
        new(_matchService, _scopeManager, _registryLookup, _logger, _documentBuffer);

    private StepRenameHandler CreateSutWithTelemetry(ILspTelemetryService telemetry) =>
        new(_matchService, _scopeManager, _registryLookup, _logger, _documentBuffer, telemetry);

    private void SetupBuffer(string csText)
    {
        _documentBuffer
            .TryGet(Arg.Any<DocumentUri>(), out Arg.Any<DocumentBuffer?>())
            .Returns(ci =>
            {
                ci[1] = new DocumentBuffer(CsUri, 1, csText);
                return true;
            });
    }

    private void SetupBuffers(params (DocumentUri Uri, string Text)[] buffers)
    {
        var map = buffers.ToDictionary(b => b.Uri.ToString(), b => b.Text);
        _documentBuffer
            .TryGet(Arg.Any<DocumentUri>(), out Arg.Any<DocumentBuffer?>())
            .Returns(ci =>
            {
                var u = (DocumentUri)ci[0];
                if (map.TryGetValue(u.ToString(), out var text))
                {
                    ci[1] = new DocumentBuffer(u, 1, text);
                    return true;
                }
                ci[1] = null;
                return false;
            });
    }

    private static ProjectStepDefinitionBinding MakeBinding(
        ScenarioBlock type,
        Regex         regex,
        string        specifiedExpression,
        int           line,
        int           column = 9,
        ProjectBindingImplementation? sharedImpl = null,
        string        method = "Steps.M()")
    {
        var impl = sharedImpl ?? new ProjectBindingImplementation(
            method, null, new SourceLocation(CsPath, line, column));
        return new ProjectStepDefinitionBinding(type, regex, null, impl, specifiedExpression);
    }

    private static RenameParams RenameAt(int line, int character, string newName) =>
        new()
        {
            TextDocument = new TextDocumentIdentifier { Uri = CsUri },
            Position     = new Position(line, character),
            NewName      = newName
        };

    // ── The F16 defect: registry expression is a discovery projection (regex), not the
    //    source literal (Cucumber expression). The attribute must still be located and
    //    edited even though `binding.Expression` does not equal the source string. ──────

    [Fact]
    public async Task Rename_edits_source_literal_even_when_registry_expression_is_a_regex_projection()
    {
        // Source uses a Cucumber expression …
        const string csText =
            "using Reqnroll;\n" +
            "namespace N\n" +
            "{\n" +
            "    [Binding]\n" +
            "    public class Steps\n" +
            "    {\n" +
            "        [Given(\"the first number is {int}\")]\n" +          // 0-based line 6
            "        public void GivenTheFirstNumberIs(int number) { }\n" + // 0-based line 7
            "    }\n" +
            "}\n";
        SetupBuffer(csText);

        // … but the registry carries the regex projection produced by discovery.
        var binding = MakeBinding(
            ScenarioBlock.Given,
            new Regex("^the first number is (.*)$"),
            specifiedExpression: "the first number is (.*)",
            line: 8, column: 9);                                  // 1-based method line
        _registryLookup.GetRegistryForUri(Arg.Any<DocumentUri>())
                       .Returns(ProjectBindingRegistry.FromBindings(new[] { binding }));

        // Position-based resolution: request maps to the binding's (line 8, col 9).
        var result = await CreateSut().HandleRenameAsync(
            RenameAt(line: 7, character: 8, newName: "the renamed number is {int}"),
            CancellationToken.None);

        result.Should().NotBeNull();
        var edits = result!.Changes![CsUri].ToList();
        edits.Should().ContainSingle();
        edits[0].NewText.Should().Be("\"the renamed number is {int}\"");
        edits[0].Range.Start.Line.Should().Be(6, "the attribute literal lives on 0-based line 6");
    }

    // ── Multiple same-type attributes on one method are disambiguated by the resolved
    //    binding's expression, not by source position. ─────────────────────────────────

    [Fact]
    public async Task Rename_multi_attribute_method_edits_only_the_selected_attribute_literal()
    {
        const string csText =
            "using Reqnroll;\n" +
            "namespace N\n" +
            "{\n" +
            "    [Binding]\n" +
            "    public class Steps\n" +
            "    {\n" +
            "        [Given(\"alpha {int}\")]\n" +    // 0-based line 6
            "        [Given(\"beta {int}\")]\n" +     // 0-based line 7
            "        public void M(int x) { }\n" +    // 0-based line 8 → 1-based 9
            "    }\n" +
            "}\n";
        SetupBuffer(csText);

        var impl = new ProjectBindingImplementation("Steps.M()", null, new SourceLocation(CsPath, 9, 9));
        var alpha = new ProjectStepDefinitionBinding(
            ScenarioBlock.Given, new Regex("^alpha (.*)$"), null, impl, "alpha {int}");
        var beta = new ProjectStepDefinitionBinding(
            ScenarioBlock.Given, new Regex("^beta (.*)$"), null, impl, "beta {int}");
        _registryLookup.GetRegistryForUri(Arg.Any<DocumentUri>())
                       .Returns(ProjectBindingRegistry.FromBindings(new[] { alpha, beta }));

        var sut = CreateSut();

        // Pick the second attribute (beta) on this method.
        await sut.HandleSelectRenameTargetAsync(
            new SelectRenameTargetParams { Uri = CsUri.ToString(), Version = 0, AttributeIndex = 1 },
            CancellationToken.None);

        var result = await sut.HandleRenameAsync(
            RenameAt(line: 8, character: 8, newName: "gamma {int}"),
            CancellationToken.None);

        result.Should().NotBeNull();
        var edits = result!.Changes![CsUri].ToList();
        edits.Should().ContainSingle();
        edits[0].NewText.Should().Be("\"gamma {int}\"");
        edits[0].Range.Start.Line.Should().Be(7, "the selected (beta) attribute literal is on 0-based line 7");
    }

    // ── Cucumber parameter types must survive the write-back, even when the dialog seeded
    //    (and the user edited) the regex projection of the expression. ────────────────────

    [Fact]
    public async Task Rename_retains_original_cucumber_parameter_type_when_new_name_uses_regex_form()
    {
        const string csText =
            "using Reqnroll;\n" +
            "namespace N\n" +
            "{\n" +
            "    [Binding]\n" +
            "    public class Steps\n" +
            "    {\n" +
            "        [Given(\"the first number is {int}\")]\n" +          // 0-based line 6
            "        public void GivenTheFirstNumberIs(int number) { }\n" +
            "    }\n" +
            "}\n";
        SetupBuffer(csText);

        var binding = MakeBinding(
            ScenarioBlock.Given,
            new Regex("^the first number is (.*)$"),
            specifiedExpression: "the first number is (.*)",
            line: 8, column: 9);
        _registryLookup.GetRegistryForUri(Arg.Any<DocumentUri>())
                       .Returns(ProjectBindingRegistry.FromBindings(new[] { binding }));

        // The user edited the regex-form seed (number → no), keeping the (.*) slot.
        var result = await CreateSut().HandleRenameAsync(
            RenameAt(line: 7, character: 8, newName: "the first no is (.*)"),
            CancellationToken.None);

        result.Should().NotBeNull();
        var edits = result!.Changes![CsUri].ToList();
        edits.Should().ContainSingle();
        edits[0].NewText.Should().Be("\"the first no is {int}\"",
            "the original Cucumber {int} parameter type is preserved, not the regex (.*) the user typed");
    }

    // ── renameTargets surfaces the live source expression so the dialog seeds the
    //    Cucumber form rather than the regex projection. ───────────────────────────────────

    [Fact]
    public async Task RenameTargets_reports_live_source_expression_not_the_regex_projection()
    {
        const string csText =
            "using Reqnroll;\n" +
            "namespace N\n" +
            "{\n" +
            "    [Binding]\n" +
            "    public class Steps\n" +
            "    {\n" +
            "        [Given(\"the first number is {int}\")]\n" +
            "        public void GivenTheFirstNumberIs(int number) { }\n" +
            "    }\n" +
            "}\n";
        SetupBuffer(csText);

        var binding = MakeBinding(
            ScenarioBlock.Given,
            new Regex("^the first number is (.*)$"),
            specifiedExpression: "the first number is (.*)",
            line: 8, column: 9);
        _registryLookup.GetRegistryForUri(Arg.Any<DocumentUri>())
                       .Returns(ProjectBindingRegistry.FromBindings(new[] { binding }));

        var response = await CreateSut().HandleRenameTargetsAsync(
            new TextDocumentPositionParams
            {
                TextDocument = new TextDocumentIdentifier { Uri = CsUri },
                Position     = new Position(7, 8)
            },
            CancellationToken.None);

        response.Should().NotBeNull();
        var target = response!.Targets.Should().ContainSingle().Subject;
        target.Expression.Should().Be("the first number is {int}");
        target.Label.Should().Be("Given the first number is {int}");
    }

    // ── ReconcileParameterTokens unit behaviour ─────────────────────────────────────────

    [Theory]
    // regex-form edit over a Cucumber source → Cucumber type retained
    [InlineData("the first number is {int}", "the first no is (.*)", "the first no is {int}")]
    // Cucumber-form edit over a Cucumber source → verbatim
    [InlineData("the first number is {int}", "the first no is {int}", "the first no is {int}")]
    // regex source stays regex
    [InlineData("the first number is (.*)", "the first no is (.*)", "the first no is (.*)")]
    // multiple params, mixed forms → each slot takes the source token positionally
    [InlineData("a {int} b {string}", "x (.*) y (.*)", "x {int} y {string}")]
    // no parameters → verbatim
    [InlineData("just text", "renamed text", "renamed text")]
    // slot-count mismatch → user text honoured verbatim
    [InlineData("a {int}", "a {int} {word}", "a {int} {word}")]
    public void ReconcileParameterTokens_preserves_original_slot_tokens(
        string source, string newName, string expected)
    {
        StepRenameHandler.ReconcileParameterTokens(source, newName).Should().Be(expected);
    }

    // ── End-to-end: a Scenario Outline placeholder usage must be preserved in the feature edit,
    //    not replaced by the binding's {int} placeholder. ─────────────────────────────────────

    [Fact]
    public async Task Rename_preserves_scenario_outline_placeholder_in_feature_edit()
    {
        const string csText =
            "using Reqnroll;\n" +
            "namespace N\n" +
            "{\n" +
            "    [Binding]\n" +
            "    public class Steps\n" +
            "    {\n" +
            "        [Given(\"the second number is {int}\")]\n" +     // 0-based line 6
            "        public void GivenTheSecondNumberIs(int number) { }\n" +
            "    }\n" +
            "}\n";

        // The feature step uses an Examples placeholder, which does not match the numeric regex.
        const string featureText =
            "Feature: F\n" +
            "Scenario Outline: x\n" +
            "\tGiven the second number is <secondNumber>\n";   // step text at line 2, chars 7..42
        var featureUri = DocumentUri.FromFileSystemPath("/workspace/x.feature");

        SetupBuffers((CsUri, csText), (featureUri, featureText));

        var binding = MakeBinding(
            ScenarioBlock.Given,
            new Regex("^the second number is (-?\\d+)$"),
            specifiedExpression: "the second number is {int}",
            line: 8, column: 9);
        _registryLookup.GetRegistryForUri(Arg.Any<DocumentUri>())
                       .Returns(ProjectBindingRegistry.FromBindings(new[] { binding }));

        var snapshot = new LspTextSnapshot(featureUri.ToString(), 1, featureText);
        var match = new StepBindingMatch(
            featureUri.ToString(),
            GherkinRange.FromPoint(snapshot, startOffset: 38, length: 35),
            MatchResult.NoMatch);
        _matchService.FindUsages(Arg.Any<SourceLocation>(), Arg.Any<IReadOnlyCollection<ProjectOwner>>())
                     .Returns(new[] { match });

        var result = await CreateSut().HandleRenameAsync(
            RenameAt(line: 7, character: 8, newName: "the second no is {int}"),
            CancellationToken.None);

        result.Should().NotBeNull();
        var featureEdits = result!.Changes![featureUri].ToList();
        featureEdits.Should().ContainSingle();
        featureEdits[0].NewText.Should().Be("the second no is <secondNumber>",
            "the outline placeholder is preserved; the binding's {int} token must not leak into the feature");
    }

    // ── Feature-file renaming tests ─────────────────────────────────────────────
    // These cover the three new code paths: HandleRenameTargetsFromFeatureAsync,
    // FindBindingsAtFeatureStep, and the .feature branch of HandleRenameAsync.

    private static FeatureBindingMatchSet MakeFeatureMatchSet(
        string featureUri, ProjectStepDefinitionBinding binding,
        string scenarioBlock, string stepText, int stepLine, int stepChar)
    {
        var text = $"Feature: F\nScenario: S\n\t{scenarioBlock} {stepText}\n";
        var snapshot = new LspTextSnapshot(featureUri, 1, text);
        var stepPrefix = $"\t{scenarioBlock} ";
        var startOffset = text.IndexOf(stepPrefix + stepText) + stepPrefix.Length;
        var range = GherkinRange.FromPoint(snapshot, startOffset: startOffset, length: stepText.Length);
        var match = new StepBindingMatch(
            featureUri,
            range,
            MatchResult.CreateMultiMatch(new[]
            {
                MatchResultItem.CreateMatch(binding, ParameterMatch.NotMatch)
            }));

        return new FeatureBindingMatchSet(
            featureUri,
            new ProjectOwner("/workspace/MyProject.csproj", ".NETCoreApp,Version=v8.0"),
            1, 1,
            new[] { match });
    }

    private static LspReqnrollProject MakeTestProject() =>
        new(
            new ReqnrollProjectLoadedParams
            {
                ProjectFile = "/workspace/MyProject.csproj",
                ProjectFolder = "/workspace",
                TargetFrameworkMoniker = ".NETCoreApp,Version=v8.0"
            },
            Substitute.For<Reqnroll.IdeSupport.Common.IIdeScope>());

    [Fact]
    public async Task RenameTargets_from_feature_returns_matched_binding()
    {
        var featureUri = DocumentUri.FromFileSystemPath("/workspace/test.feature");
        var binding = MakeBinding(
            ScenarioBlock.Then,
            new Regex("^to be or not to be$"),
            specifiedExpression: "to be or not to be",
            line: 8, column: 9,
            method: "Steps.ThenToBeOrNotToBe()");
        _registryLookup.GetRegistryForUri(Arg.Any<DocumentUri>())
                       .Returns(ProjectBindingRegistry.FromBindings(new[] { binding }));

        var project = MakeTestProject();
        _scopeManager.ResolveOwners(featureUri).Returns(new[] { project });

        var matchSet = MakeFeatureMatchSet(
            featureUri.ToString(), binding,
            "Then", "to be or not to be", stepLine: 2, stepChar: 5);
        _matchService.TryGet(Arg.Any<MatchSetKey>(), out Arg.Any<FeatureBindingMatchSet>())
            .Returns(ci =>
            {
                ci[1] = matchSet;
                return true;
            });

        var response = await CreateSut().HandleRenameTargetsAsync(
            new TextDocumentPositionParams
            {
                TextDocument = new TextDocumentIdentifier { Uri = featureUri },
                Position = new Position(2, 14)
            },
            CancellationToken.None);

        response.Should().NotBeNull();
        var target = response!.Targets.Should().ContainSingle().Subject;
        target.Expression.Should().Be("to be or not to be");
        target.Label.Should().Be("Then to be or not to be");
    }

    [Fact]
    public async Task RenameTargets_from_feature_no_match_returns_empty_response()
    {
        var featureUri = DocumentUri.FromFileSystemPath("/workspace/test.feature");
        _scopeManager.ResolveOwners(featureUri).Returns(Array.Empty<LspReqnrollProject>());

        var response = await CreateSut().HandleRenameTargetsAsync(
            new TextDocumentPositionParams
            {
                TextDocument = new TextDocumentIdentifier { Uri = featureUri },
                Position = new Position(2, 14)
            },
            CancellationToken.None);

        response.Should().NotBeNull();
        response!.Targets.Should().BeEmpty();
    }

    [Fact]
    public async Task Rename_from_feature_edits_both_feature_text_and_csharp_attribute()
    {
        var featureUri = DocumentUri.FromFileSystemPath("/workspace/test.feature");
        var csUri = DocumentUri.FromFileSystemPath("/workspace/Steps.cs");

        const string csText =
            "using Reqnroll;\n" +
            "namespace N\n" +
            "{\n" +
            "    [Binding]\n" +
            "    public class Steps\n" +
            "    {\n" +
            "        [Then(\"to be or not to be\")]\n" +
            "        public void ThenToBeOrNotToBe() { }\n" +
            "    }\n" +
            "}\n";

        SetupBuffers((csUri, csText));

        var binding = MakeBinding(
            ScenarioBlock.Then,
            new Regex("^to be or not to be$"),
            specifiedExpression: "to be or not to be",
            line: 8, column: 9,
            method: "Steps.ThenToBeOrNotToBe()");
        _registryLookup.GetRegistryForUri(Arg.Any<DocumentUri>())
                       .Returns(ProjectBindingRegistry.FromBindings(new[] { binding }));

        var project = MakeTestProject();
        _scopeManager.ResolveOwners(featureUri).Returns(new[] { project });

        var matchSet = MakeFeatureMatchSet(
            featureUri.ToString(), binding,
            "Then", "to be or not to be", stepLine: 2, stepChar: 5);
        _matchService.TryGet(Arg.Any<MatchSetKey>(), out Arg.Any<FeatureBindingMatchSet>())
            .Returns(ci =>
            {
                ci[1] = matchSet;
                return true;
            });

        const string featureText = "Feature: F\nScenario: S\n\tThen to be or not to be\n";
        var snapshot = new LspTextSnapshot(featureUri.ToString(), 1, featureText);
        const string stepText = "to be or not to be";
        var stepOffset = featureText.IndexOf("\tThen " + stepText) + "\tThen ".Length;
        var usageMatch = new StepBindingMatch(
            featureUri.ToString(),
            GherkinRange.FromPoint(snapshot, startOffset: stepOffset, length: stepText.Length),
            MatchResult.CreateMultiMatch(new[]
            {
                MatchResultItem.CreateMatch(binding, ParameterMatch.NotMatch)
            }));
        _matchService.FindUsages(Arg.Any<SourceLocation>(), Arg.Any<IReadOnlyCollection<ProjectOwner>>())
                     .Returns(new[] { usageMatch });

        var sut = CreateSut();
        await sut.HandleSelectRenameTargetAsync(
            new SelectRenameTargetParams { Uri = featureUri.ToString(), Version = 0, AttributeIndex = 0 },
            CancellationToken.None);

        var result = await sut.HandleRenameAsync(
            new RenameParams
            {
                TextDocument = new TextDocumentIdentifier { Uri = featureUri },
                Position = new Position(2, 14),
                NewName = "to be and not to be"
            },
            CancellationToken.None);

        result.Should().NotBeNull();
        result!.Changes!.Should().ContainKey(featureUri);
        result.Changes.Should().ContainKey(csUri);

        var featureEdit = result.Changes[featureUri].ToList();
        featureEdit.Should().ContainSingle();
        featureEdit[0].NewText.Should().Be("to be and not to be");

        var csEdit = result.Changes[csUri].ToList();
        csEdit.Should().ContainSingle();
        csEdit[0].NewText.Should().Be("\"to be and not to be\"");
    }

    [Fact]
    public async Task Rename_from_feature_without_session_resolves_binding_via_match_cache()
    {
        var featureUri = DocumentUri.FromFileSystemPath("/workspace/test.feature");
        var csUri = DocumentUri.FromFileSystemPath("/workspace/Steps.cs");

        const string csText =
            "using Reqnroll;\n" +
            "namespace N\n" +
            "{\n" +
            "    [Binding]\n" +
            "    public class Steps\n" +
            "    {\n" +
            "        [Given(\"I press add\")]\n" +
            "        public void GivenIPressAdd() { }\n" +
            "    }\n" +
            "}\n";

        SetupBuffers((csUri, csText));

        var binding = MakeBinding(
            ScenarioBlock.Given,
            new Regex("^I press add$"),
            specifiedExpression: "I press add",
            line: 8, column: 9,
            method: "Steps.GivenIPressAdd()");
        _registryLookup.GetRegistryForUri(Arg.Any<DocumentUri>())
                       .Returns(ProjectBindingRegistry.FromBindings(new[] { binding }));

        var project = MakeTestProject();
        _scopeManager.ResolveOwners(featureUri).Returns(new[] { project });

        var matchSet = MakeFeatureMatchSet(
            featureUri.ToString(), binding,
            "Given", "I press add", stepLine: 2, stepChar: 5);
        _matchService.TryGet(Arg.Any<MatchSetKey>(), out Arg.Any<FeatureBindingMatchSet>())
            .Returns(ci =>
            {
                ci[1] = matchSet;
                return true;
            });

        const string featureText = "Feature: F\nScenario: S\n\tGiven I press add\n";
        var snapshot = new LspTextSnapshot(featureUri.ToString(), 1, featureText);
        const string stepText = "I press add";
        var stepOffset = featureText.IndexOf("\tGiven " + stepText) + "\tGiven ".Length;
        var usageMatch = new StepBindingMatch(
            featureUri.ToString(),
            GherkinRange.FromPoint(snapshot, startOffset: stepOffset, length: stepText.Length),
            MatchResult.CreateMultiMatch(new[]
            {
                MatchResultItem.CreateMatch(binding, ParameterMatch.NotMatch)
            }));
        _matchService.FindUsages(Arg.Any<SourceLocation>(), Arg.Any<IReadOnlyCollection<ProjectOwner>>())
                     .Returns(new[] { usageMatch });

        var sut = CreateSut();

        var result = await sut.HandleRenameAsync(
            new RenameParams
            {
                TextDocument = new TextDocumentIdentifier { Uri = featureUri },
                Position = new Position(2, 10),
                NewName = "I choose add"
            },
            CancellationToken.None);

        result.Should().NotBeNull();
        result!.Changes!.Should().ContainKey(featureUri);
        result.Changes.Should().ContainKey(csUri);
    }

    [Fact]
    public async Task FindAttributeLiteralAsync_redirects_from_feature_to_csharp_source()
    {
        var featureUri = DocumentUri.FromFileSystemPath("/workspace/test.feature");
        var csUri = DocumentUri.FromFileSystemPath("/workspace/Steps.cs");
        var csPath = csUri.GetFileSystemPath();

        const string csText =
            "using Reqnroll;\n" +
            "namespace N\n" +
            "{\n" +
            "    [Binding]\n" +
            "    public class Steps\n" +
            "    {\n" +
            "        [When(\"something happens\")]\n" +
            "        public void WhenSomethingHappens() { }\n" +
            "    }\n" +
            "}\n";

        SetupBuffers((csUri, csText));

        var binding = MakeBinding(
            ScenarioBlock.When,
            new Regex("^something happens$"),
            specifiedExpression: "something happens",
            line: 8, column: 9,
            method: "Steps.WhenSomethingHappens()");

        binding = new ProjectStepDefinitionBinding(
            binding.StepDefinitionType,
            binding.Regex,
            binding.Scope,
            new ProjectBindingImplementation(
                "Steps.WhenSomethingHappens()",
                null,
                new SourceLocation(csPath, 8, 9)),
            binding.Expression);

        _registryLookup.GetRegistryForUri(Arg.Any<DocumentUri>())
                       .Returns(ProjectBindingRegistry.FromBindings(new[] { binding }));

        var project = MakeTestProject();
        _scopeManager.ResolveOwners(featureUri).Returns(new[] { project });

        var matchSet = MakeFeatureMatchSet(
            featureUri.ToString(), binding,
            "When", "something happens", stepLine: 2, stepChar: 5);
        _matchService.TryGet(Arg.Any<MatchSetKey>(), out Arg.Any<FeatureBindingMatchSet>())
            .Returns(ci =>
            {
                ci[1] = matchSet;
                return true;
            });

        const string whenFeatureText = "Feature: F\nScenario: S\n\tWhen something happens\n";
        const string whenStepText = "something happens";
        var whenStepOffset = whenFeatureText.IndexOf("\tWhen " + whenStepText) + "\tWhen ".Length;
        var usageMatch = new StepBindingMatch(
            featureUri.ToString(),
            GherkinRange.FromPoint(
                new LspTextSnapshot(featureUri.ToString(), 1, whenFeatureText),
                startOffset: whenStepOffset, length: whenStepText.Length),
            MatchResult.CreateMultiMatch(new[] { MatchResultItem.CreateMatch(binding, ParameterMatch.NotMatch) }));
        _matchService.FindUsages(Arg.Any<SourceLocation>(), Arg.Any<IReadOnlyCollection<ProjectOwner>>())
                     .Returns(new[] { usageMatch });

        var sut = CreateSut();
        await sut.HandleSelectRenameTargetAsync(
            new SelectRenameTargetParams { Uri = featureUri.ToString(), Version = 0, AttributeIndex = 0 },
            CancellationToken.None);

        var result = await sut.HandleRenameAsync(
            new RenameParams
            {
                TextDocument = new TextDocumentIdentifier { Uri = featureUri },
                Position = new Position(2, 10),
                NewName = "something changed"
            },
            CancellationToken.None);

        result.Should().NotBeNull();
        result!.Changes!.Should().ContainKey(csUri);

        var csEdit = result.Changes[csUri].ToList();
        csEdit.Should().ContainSingle();
        csEdit[0].NewText.Should().Be("\"something changed\"");
        csEdit[0].Range.Start.Line.Should().Be(6, "the [When] attribute literal is on 0-based line 6");
    }

    // ── Telemetry ───────────────────────────────────────────────────────────────────

    [Fact]
    public async Task HandleRenameAsync_emits_rename_telemetry_on_success()
    {
        // Arrange — set up a minimal rename that produces an edit
        const string csText = """
            using Reqnroll;
            namespace S;
            class C
            {
                [When("something")]
                void M() { }
            }
            """;
        var binding = new ProjectStepDefinitionBinding(
            ScenarioBlock.When,
            new Regex("^something$"),
            null,
            new ProjectBindingImplementation("C.M", null, new SourceLocation(CsPath, 5, 1)),
            "something");
        var registry = ProjectBindingRegistry.FromBindings(new[] { binding });
        _registryLookup.GetRegistryForUri(CsUri).Returns(registry);
        SetupBuffer(csText);

        var telemetry = Substitute.For<ILspTelemetryService>();
        var sut = CreateSutWithTelemetry(telemetry);

        var result = await sut.HandleRenameAsync(
            new RenameParams
            {
                TextDocument = new TextDocumentIdentifier { Uri = CsUri },
                Position = new Position(4, 0),
                NewName = "something changed"
            },
            CancellationToken.None);

        result.Should().NotBeNull();
        telemetry.Received(1).SendEvent(
            "Rename step command executed",
            Arg.Is<Dictionary<string, object?>>(d => false.Equals(d["Erroneous"])));
    }
}
