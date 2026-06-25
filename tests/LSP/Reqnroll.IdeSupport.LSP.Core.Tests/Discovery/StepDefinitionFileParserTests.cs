#nullable disable

namespace Reqnroll.IdeSupport.LSP.Core.Tests.Discovery;

/// <summary>
/// Unit tests for the Roslyn-based (source-level) binding discovery (design doc feature F2).
/// These exercise the critical paths directly against <see cref="StepDefinitionFileParser"/>
/// without requiring the connector / discovery pipeline.
/// </summary>
public class StepDefinitionFileParserTests
{
    private const string FilePath = @"C:\Project\Steps.cs";

    private static Task<StepDefinitionFileBindings> ParseBindings(string body)
    {
        var content = $@"
using Reqnroll;
namespace TestProject
{{
    [Binding]
    public class Steps
    {{
{body}
    }}
}}";
        var file = FileDetails.FromPath(FilePath).WithCSharpContent(content);
        return new StepDefinitionFileParser().ParseBindings(file);
    }

    private static async Task<List<ProjectStepDefinitionBinding>> ParseStepDefinitions(string body) =>
        (await ParseBindings(body)).StepDefinitions.ToList();

    [Theory]
    [InlineData("Given", ScenarioBlock.Given)]
    [InlineData("When", ScenarioBlock.When)]
    [InlineData("Then", ScenarioBlock.Then)]
    public async Task Discovers_each_step_definition_attribute_kind(string attribute, ScenarioBlock expectedBlock)
    {
        var stepDefinitions = await ParseStepDefinitions(
            $@"[{attribute}(""I do something"")]
               public void Method() {{ }}");

        stepDefinitions.Should().ContainSingle();
        var binding = stepDefinitions[0];
        binding.StepDefinitionType.Should().Be(expectedBlock);
        binding.Expression.Should().Be("I do something");
        binding.Regex.ToString().Should().Be("^I do something$");
        binding.IsValid.Should().BeTrue();
        binding.Implementation.Method.Should().Be("TestProject.Steps.Method");
    }

    [Fact]
    public async Task StepDefinition_attribute_registers_for_all_three_blocks()
    {
        var stepDefinitions = await ParseStepDefinitions(
            @"[StepDefinition(""shared step"")]
              public void Method() { }");

        stepDefinitions.Select(b => b.StepDefinitionType)
            .Should().BeEquivalentTo(new[] { ScenarioBlock.Given, ScenarioBlock.When, ScenarioBlock.Then });
        stepDefinitions.Should().OnlyContain(b => b.Expression == "shared step");
    }

    [Fact]
    public async Task Discovers_multiple_attributes_on_one_method()
    {
        var stepDefinitions = await ParseStepDefinitions(
            @"[Given(""a"")]
              [When(""a"")]
              public void Method() { }");

        stepDefinitions.Select(b => b.StepDefinitionType)
            .Should().BeEquivalentTo(new[] { ScenarioBlock.Given, ScenarioBlock.When });
    }

    [Theory]
    [InlineData("GivenAttribute")]
    [InlineData("Reqnroll.Given")]
    [InlineData("global::Reqnroll.GivenAttribute")]
    public async Task Recognizes_qualified_and_suffixed_attribute_names(string attribute)
    {
        var stepDefinitions = await ParseStepDefinitions(
            $@"[{attribute}(""qualified"")]
               public void Method() {{ }}");

        stepDefinitions.Should().ContainSingle()
            .Which.StepDefinitionType.Should().Be(ScenarioBlock.Given);
    }

    [Theory]
    [InlineData("[Binding]")]
    [InlineData("[Obsolete]")]
    [InlineData("[Test]")]
    [InlineData("[CustomThing(\"x\", 5)]")]
    public async Task Ignores_unrelated_attributes_without_throwing(string attribute)
    {
        var stepDefinitions = await ParseStepDefinitions(
            $@"{attribute}
               public void Method() {{ }}");

        stepDefinitions.Should().BeEmpty();
    }

    [Fact]
    public async Task Handles_expression_bodied_method_without_crashing()
    {
        var stepDefinitions = await ParseStepDefinitions(
            @"[When(""pressed"")]
              public void Method() => System.Console.WriteLine(""x"");");

        var binding = stepDefinitions.Should().ContainSingle().Subject;
        binding.Expression.Should().Be("pressed");
        binding.Implementation.SourceLocation.Should().NotBeNull();
    }

    [Fact]
    public async Task Reads_expression_from_named_argument()
    {
        var stepDefinitions = await ParseStepDefinitions(
            @"[When(Expression = ""named expression"")]
              public void Method() { }");

        stepDefinitions.Should().ContainSingle()
            .Which.Expression.Should().Be("named expression");
    }

    [Fact]
    public async Task Ignores_extra_named_arguments_alongside_expression()
    {
        var stepDefinitions = await ParseStepDefinitions(
            @"[Given(""the expression"", Culture = ""en-US"")]
              public void Method() { }");

        stepDefinitions.Should().ContainSingle()
            .Which.Expression.Should().Be("the expression");
    }

    [Fact]
    public async Task Reads_verbatim_string_expression()
    {
        var stepDefinitions = await ParseStepDefinitions(
            @"[Given(@""path \to\ thing"")]
              public void Method() { }");

        stepDefinitions.Should().ContainSingle()
            .Which.Expression.Should().Be(@"path \to\ thing");
    }

    [Fact]
    public async Task Parameterless_attribute_yields_present_but_non_matching_binding()
    {
        var stepDefinitions = await ParseStepDefinitions(
            @"[When]
              public void Method() { }");

        var binding = stepDefinitions.Should().ContainSingle().Subject;
        binding.StepDefinitionType.Should().Be(ScenarioBlock.When);
        binding.Regex.Should().BeNull();
        binding.IsValid.Should().BeFalse("a binding without an expression cannot match any step");
    }

    [Fact]
    public async Task Captures_parameter_types()
    {
        var stepDefinitions = await ParseStepDefinitions(
            @"[Given(""I have (\d+) and (.*)"")]
              public void Method(int count, string name) { }");

        stepDefinitions.Should().ContainSingle()
            .Which.Implementation.ParameterTypes.Should().Equal("int", "string");
    }

    [Theory]
    [InlineData("Given", @"the number is {int}",         "the number is 42",      @"(-?\d+)")]
    [InlineData("When",  @"the value is {float}",        "the value is 3.14",     @"(-?\d*(?:\.\d+)?)")]
    [InlineData("Then",  @"the word is {word}",          "the word is hello",     @"(\w+)")]
    public async Task Standard_cucumber_param_types_are_converted_to_regex(
        string keyword, string expression, string stepText, string expectedGroupPattern)
    {
        var stepDefinitions = await ParseStepDefinitions(
            $@"[{keyword}(""{expression}"")]
               public void Method() {{ }}");

        var binding = stepDefinitions.Should().ContainSingle().Subject;
        binding.IsValid.Should().BeTrue();
        binding.Regex.ToString().Should().Contain(expectedGroupPattern);
        binding.Regex.IsMatch(stepText).Should().BeTrue(
            $"the converted regex should match the step text '{stepText}'");
    }

    [Fact]
    public async Task Custom_cucumber_param_type_falls_back_to_wildcard_and_matches_step()
    {
        // Reproduces the live bug: [When("the two numbers {Verb} added")] uses a custom
        // step-argument transformation type 'Verb'. Without proper conversion the Roslyn
        // binding's regex was ^the two numbers {Verb} added$, which matches the literal
        // text "{Verb}" rather than an actual value — causing the When step to appear
        // unbound even though only the Given expression was edited.
        var stepDefinitions = await ParseStepDefinitions(
            @"[When(""the two numbers {Verb} added"")]
              public void Method(string verb) { }");

        var binding = stepDefinitions.Should().ContainSingle().Subject;
        binding.IsValid.Should().BeTrue();
        binding.Regex.ToString().Should().Contain(@"(.*)");
        binding.Regex.IsMatch("the two numbers 'are' added").Should().BeTrue();
        binding.Regex.IsMatch("the two numbers were added").Should().BeTrue();
    }

    [Fact]
    public async Task Source_location_is_zero_width_range_at_method_identifier()
    {
        // LSP convention: definition range is the identifier span, not the full declaration.
        // Start and end must be the same position (zero-width).
        var stepDefinitions = await ParseStepDefinitions(
            @"[Given(""x"")]
              public void Method()
              {
              }");

        var location = stepDefinitions.Should().ContainSingle().Subject.Implementation.SourceLocation;
        location.SourceFile.Should().Be(FilePath);
        location.HasEndPosition.Should().BeTrue();
        location.SourceFileLine.Should().Be(location.SourceFileEndLine!.Value);
        location.SourceFileColumn.Should().Be(location.SourceFileEndColumn!.Value);
    }

    [Fact]
    public async Task Source_location_points_to_method_name_not_attribute_or_body()
    {
        // Attribute on body-line 1 (template line 8), method identifier on body-line 2 (template line 9).
        // The location must land on "TargetMethod", not on "[Given]" (line 8) or on "{" (line 10).
        // Template adds 7 header lines before the body, so:
        //   line 8  = [Given("x")]
        //   line 9  = public void TargetMethod() { }   <- identifier at column 13 (1-based)
        var stepDefinitions = await ParseStepDefinitions(
            "[Given(\"x\")]\npublic void TargetMethod() { }");

        var location = stepDefinitions.Should().ContainSingle().Subject.Implementation.SourceLocation;
        location.SourceFileLine.Should().Be(9);      // method signature line, not attribute (line 8)
        location.SourceFileColumn.Should().Be(13);   // "public void " = 12 chars → identifier at col 13
        location.SourceFileEndLine.Should().Be(9);
        location.SourceFileEndColumn.Should().Be(13);
    }

    [Fact]
    public async Task Discovers_scope_on_method()
    {
        var stepDefinitions = await ParseStepDefinitions(
            @"[Scope(Tag = ""@web"")]
              [Given(""scoped"")]
              public void Method() { }");

        var scope = stepDefinitions.Should().ContainSingle().Subject.Scope;
        scope.Should().NotBeNull();
        scope.Tag.Should().NotBeNull();
        scope.Tag.Evaluate(new[] { "@web" }).Should().BeTrue();
        scope.Tag.Evaluate(new[] { "@other" }).Should().BeFalse();
    }

    [Fact]
    public async Task Combines_class_level_and_method_level_scope()
    {
        // Class scope @ui AND method scope @smoke -> only matches when both tags are present.
        var content = @"
namespace TestProject
{
    [Binding]
    [Scope(Tag = ""@ui"")]
    public class Steps
    {
        [Scope(Tag = ""@smoke"")]
        [Given(""scoped"")]
        public void Method() { }
    }
}";
        var file = FileDetails.FromPath(FilePath).WithCSharpContent(content);
        var bindings = await new StepDefinitionFileParser().ParseBindings(file);

        var scope = bindings.StepDefinitions.Should().ContainSingle().Subject.Scope;
        scope.Tag.Evaluate(new[] { "@ui", "@smoke" }).Should().BeTrue();
        scope.Tag.Evaluate(new[] { "@ui" }).Should().BeFalse();
        scope.Tag.Evaluate(new[] { "@smoke" }).Should().BeFalse();
    }

    [Fact]
    public async Task Discovers_hooks_with_type()
    {
        var bindings = await ParseBindings(
            @"[BeforeScenario]
              public void Setup() { }
              [AfterScenario]
              public void Teardown() { }");

        bindings.Hooks.Select(h => h.HookType)
            .Should().BeEquivalentTo(new[] { HookType.BeforeScenario, HookType.AfterScenario });
        bindings.StepDefinitions.Should().BeEmpty();
    }

    [Fact]
    public async Task Before_and_after_are_synonyms_for_scenario_hooks()
    {
        var bindings = await ParseBindings(
            @"[Before]
              public void Setup() { }
              [After]
              public void Teardown() { }");

        bindings.Hooks.Select(h => h.HookType)
            .Should().BeEquivalentTo(new[] { HookType.BeforeScenario, HookType.AfterScenario });
    }

    [Fact]
    public async Task Reads_hook_order()
    {
        var bindings = await ParseBindings(
            @"[BeforeScenario(Order = 42)]
              public void Setup() { }");

        bindings.Hooks.Should().ContainSingle().Which.HookOrder.Should().Be(42);
    }

    [Fact]
    public async Task Default_hook_order_is_applied_when_unspecified()
    {
        var bindings = await ParseBindings(
            @"[BeforeScenario]
              public void Setup() { }");

        bindings.Hooks.Should().ContainSingle()
            .Which.HookOrder.Should().Be(ProjectHookBinding.DefaultHookOrder);
    }

    [Fact]
    public async Task Discovers_hook_tags_as_scope()
    {
        var bindings = await ParseBindings(
            @"[BeforeScenario(""web"", ""smoke"")]
              public void Setup() { }");

        var scope = bindings.Hooks.Should().ContainSingle().Subject.Scope;
        scope.Should().NotBeNull();
        scope.Tag.Evaluate(new[] { "@web" }).Should().BeTrue();
        scope.Tag.Evaluate(new[] { "@smoke" }).Should().BeTrue();
        scope.Tag.Evaluate(new[] { "@other" }).Should().BeFalse();
    }

    [Fact]
    public async Task Discovers_bindings_in_file_scoped_namespace()
    {
        var content = @"
namespace TestProject;

[Binding]
public class Steps
{
    [Given(""x"")]
    public void Method() { }
}";
        var file = FileDetails.FromPath(FilePath).WithCSharpContent(content);
        var bindings = await new StepDefinitionFileParser().ParseBindings(file);

        bindings.StepDefinitions.Should().ContainSingle()
            .Which.Implementation.Method.Should().Be("TestProject.Steps.Method");
    }

    [Fact]
    public async Task Discovers_bindings_without_namespace()
    {
        var content = @"
[Binding]
public class Steps
{
    [Given(""x"")]
    public void Method() { }
}";
        var file = FileDetails.FromPath(FilePath).WithCSharpContent(content);
        var bindings = await new StepDefinitionFileParser().ParseBindings(file);

        bindings.StepDefinitions.Should().ContainSingle()
            .Which.Implementation.Method.Should().Be("Steps.Method");
    }

    [Fact(Skip = "Documents a known F2 limitation: Roslyn (source-level) discovery is syntactic " +
                 "only and cannot resolve attributes that derive from a Reqnroll binding attribute. " +
                 "These are still discovered by the reflection Connector after a build. See the F2 " +
                 "'Known limitations' section in docs/LSP-IDE-Support-Design.md. Not addressed at this time.")]
    public async Task Does_not_discover_custom_attribute_derived_from_reqnroll_attribute()
    {
        // GivenWebAttribute derives from GivenAttribute, but the parser only matches by attribute
        // name (no semantic model), so [GivenWeb] is not recognized as a Given step definition.
        var content = @"
namespace TestProject
{
    public class GivenWebAttribute : GivenAttribute
    {
        public GivenWebAttribute(string expression) : base(expression) { }
    }

    [Binding]
    public class Steps
    {
        [GivenWeb(""I am on the web"")]
        public void Method() { }
    }
}";
        var file = FileDetails.FromPath(FilePath).WithCSharpContent(content);
        var bindings = await new StepDefinitionFileParser().ParseBindings(file);

        // Desired (currently unsupported) behaviour:
        bindings.StepDefinitions.Should().ContainSingle()
            .Which.StepDefinitionType.Should().Be(ScenarioBlock.Given);
    }

    [Fact]
    public async Task ReplaceBindings_replaces_step_definitions_and_hooks_for_a_single_file_only()
    {
        const string otherFilePath = @"C:\Project\Other.cs";

        var changedFile = FileDetails.FromPath(FilePath).WithCSharpContent(@"
namespace TestProject
{
    [Binding]
    public class Changed
    {
        [When(""new expression"")]
        public void Method() { }

        [BeforeScenario]
        public void Setup() { }
    }
}");

        // Registry pre-populated with stale bindings for the changed file plus bindings owned by another file.
        var registry = new ProjectBindingRegistry(
            new[]
            {
                BuildStepDefinition("^stale$", "Changed.Method", FilePath),
                BuildStepDefinition("^other$", "Other.Method", otherFilePath)
            },
            new[]
            {
                BuildHook(HookType.AfterScenario, "Changed.OldHook", FilePath),
                BuildHook(HookType.BeforeFeature, "Other.Hook", otherFilePath)
            },
            projectHash: 0);

        var updated = await registry.ReplaceBindings(changedFile);

        // The changed file's bindings are rediscovered from source...
        updated.StepDefinitions
            .Should().ContainSingle(b => b.Implementation.SourceLocation.SourceFile == FilePath)
            .Which.Regex.ToString().Should().Be("^new expression$");
        updated.Hooks
            .Should().ContainSingle(h => h.Implementation.SourceLocation.SourceFile == FilePath)
            .Which.HookType.Should().Be(HookType.BeforeScenario);

        // ...while bindings owned by other files are left untouched.
        updated.StepDefinitions.Should().ContainSingle(b => b.Implementation.SourceLocation.SourceFile == otherFilePath);
        updated.Hooks.Should().ContainSingle(h => h.Implementation.SourceLocation.SourceFile == otherFilePath);
    }

    [Fact]
    public async Task ReplaceBindings_replaces_same_file_bindings_despite_drive_letter_case()
    {
        // Reproduces the live bug: the reflection connector records the source path with an
        // upper-case drive letter (from the PDB), while the Roslyn update arrives via a document
        // URI carrying a lower-case drive letter. They are the same physical file and the stale
        // binding must be replaced — otherwise the step keeps matching the old expression.
        const string connectorPath = @"C:\Project\Steps.cs";
        var changedFile = FileDetails.FromPath(@"c:\Project\Steps.cs").WithCSharpContent(@"
namespace S
{
    [Binding]
    public class Steps
    {
        [Given(""the firs number is (.*)"")]
        public void Method(int n) { }
    }
}");

        var registry = new ProjectBindingRegistry(
            new[] { BuildStepDefinition("^the first number is (.*)$", "S.Steps.Method", connectorPath) },
            Array.Empty<ProjectHookBinding>(),
            projectHash: 0);

        var updated = await registry.ReplaceBindings(changedFile);

        updated.StepDefinitions.Should().ContainSingle("the stale upper-case-drive binding must be removed")
            .Which.Regex!.ToString().Should().Be("^the firs number is (.*)$");
    }

    private static ProjectStepDefinitionBinding BuildStepDefinition(string regex, string method, string sourceFile) =>
        new(ScenarioBlock.Given, new Regex(regex), null,
            new ProjectBindingImplementation(method, Array.Empty<string>(), new SourceLocation(sourceFile, 0, 0)));

    private static ProjectHookBinding BuildHook(HookType hookType, string method, string sourceFile) =>
        new(new ProjectBindingImplementation(method, Array.Empty<string>(), new SourceLocation(sourceFile, 0, 0)),
            null, hookType, null, null);
}
