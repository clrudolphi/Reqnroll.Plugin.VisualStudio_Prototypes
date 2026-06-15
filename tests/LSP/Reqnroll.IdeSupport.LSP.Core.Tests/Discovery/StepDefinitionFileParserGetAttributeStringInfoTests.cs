#nullable disable

namespace Reqnroll.IdeSupport.LSP.Core.Tests.Discovery;

/// <summary>
/// Unit tests for <see cref="StepDefinitionFileParser.GetAttributeStringInfo"/> — the
/// targeted method that locates a binding attribute's string-literal argument at a given
/// source position and returns span, syntax kind, and raw text.
/// </summary>
public class StepDefinitionFileParserGetAttributeStringInfoTests
{
    private const string FilePath = @"C:\Project\Steps.cs";

    /// <summary>
    /// Builds a parseable C# file wrapping the given method body in a binding class.
    /// The template adds 7 header lines before the body:
    /// <code>
    /// (empty line)
    /// using Reqnroll;
    /// namespace TestProject
    /// {
    ///     [Binding]
    ///     public class Steps
    ///     {
    /// {body}              &lt;-- method content starts at line 8
    ///     }
    /// }
    /// </code>
    /// </summary>
    private static CSharpStepDefinitionFile CreateFile(string body)
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
        return FileDetails.FromPath(FilePath).WithCSharpContent(content);
    }

    // -------------------------------------------------------------------------------
    //  Helper: line / column for the method identifier
    // -------------------------------------------------------------------------------
    // Body with 4-space indentation:
    //   line 8:     [Given("I press add")]
    //   line 9:     public void Method() { }
    //
    // "Method" identifier column = 4 spaces + "public void " (12 chars) = 16 → 1-indexed 17.

    [Fact]
    public async Task GetAttributeStringInfo_regular_string_returns_span_and_text()
    {
        var body = "    [Given(\"I press add\")]\n    public void Method() { }";
        var file = CreateFile(body);
        var parser = new StepDefinitionFileParser();

        var info = await parser.GetAttributeStringInfo(
            file, methodLine: 9, methodColumn: 17, attributeIndex: 0, expression: "I press add");

        info.Should().NotBeNull();
        info.RawText.Should().Be("\"I press add\"");
        info.LiteralKind.Should().Be(SyntaxKind.StringLiteralToken);
    }

    [Fact]
    public async Task GetAttributeStringInfo_verbatim_string_returns_correct_kind()
    {
        var body = "    [When(@\"I press add\")]\n    public void Method() { }";
        var file = CreateFile(body);
        var parser = new StepDefinitionFileParser();

        var info = await parser.GetAttributeStringInfo(
            file, methodLine: 9, methodColumn: 17, attributeIndex: 0, expression: "I press add");

        info.Should().NotBeNull();
        // Roslyn represents both regular and verbatim strings as StringLiteralToken.
        info.LiteralKind.Should().Be(SyntaxKind.StringLiteralToken);
    }

    [Fact]
    public async Task GetAttributeStringInfo_constant_expression_returns_null()
    {
        var body = "    [Given(MyConst)]\n    public void Method() { }";
        var file = CreateFile(body);
        var parser = new StepDefinitionFileParser();

        var info = await parser.GetAttributeStringInfo(
            file, methodLine: 9, methodColumn: 17, attributeIndex: 0, expression: null);

        // No LiteralExpressionSyntax exists in the attribute argument — only an identifier.
        info.Should().BeNull();
    }

    [Fact]
    public async Task GetAttributeStringInfo_wrong_attribute_index_returns_null()
    {
        var body = "    [Given(\"a\")]\n    [When(\"b\")]\n    [Then(\"c\")]\n    public void Method() { }";
        var file = CreateFile(body);
        var parser = new StepDefinitionFileParser();

        // Method identifier on line 11 (body adds 3 attribute lines above the method).
        // Only 3 attributes exist; index 5 is out of range.
        var info = await parser.GetAttributeStringInfo(
            file, methodLine: 11, methodColumn: 17, attributeIndex: 5, expression: null);

        info.Should().BeNull();
    }

    [Fact]
    public async Task GetAttributeStringInfo_wrong_method_line_returns_null()
    {
        var body = "    [Given(\"I press add\")]\n    public void Method() { }";
        var file = CreateFile(body);
        var parser = new StepDefinitionFileParser();

        var info = await parser.GetAttributeStringInfo(
            file, methodLine: 99, methodColumn: 17, attributeIndex: 0, expression: "I press add");

        info.Should().BeNull();
    }

    [Fact]
    public async Task GetAttributeStringInfo_wrong_expression_returns_first_literal()
    {
        // GetAttributeStringInfo does NOT filter by the expression parameter — it
        // returns the first string-literal argument found regardless of content.
        var body = "    [Given(\"I press add\")]\n    public void Method() { }";
        var file = CreateFile(body);
        var parser = new StepDefinitionFileParser();

        var info = await parser.GetAttributeStringInfo(
            file, methodLine: 9, methodColumn: 17, attributeIndex: 0, expression: "non-existent expression");

        info.Should().NotBeNull();
        info.RawText.Should().Be("\"I press add\"");
        info.LiteralKind.Should().Be(SyntaxKind.StringLiteralToken);
    }
}
