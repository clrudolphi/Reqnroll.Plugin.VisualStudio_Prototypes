#nullable disable

using Cucumber.TagExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;
using Reqnroll.IdeSupport.LSP.Core.Discovery.TagExpressions;
using Reqnroll.IdeSupport.LSP.Core.Gherkin.Parsing;
using System.Text;
using System.Threading.Tasks;

namespace Reqnroll.IdeSupport.LSP.Core.Discovery;

/// <summary>
/// Roslyn-based (source-level) binding discovery (design doc feature F2). Scans a single
/// C# syntax tree for Reqnroll step-definition and hook attributes and produces the
/// corresponding <see cref="ProjectStepDefinitionBinding"/> / <see cref="ProjectHookBinding"/>
/// instances. Discovery is syntax-only — no compilation or build is required — so it can run
/// immediately as the user edits a <c>.cs</c> file.
/// </summary>
public class StepDefinitionFileParser
{
    private const string AttributeSuffix = "Attribute";

    private static readonly ReqnrollTagExpressionParser TagExpressionParser = new();

    /// <summary>
    /// Matches a Cucumber <c>{paramType}</c> placeholder where the parameter name is a simple
    /// word identifier. Used to convert Cucumber expressions to regex before matching.
    /// </summary>
    private static readonly Regex CucumberParamPattern =
        new(@"\{(\w+)\}", RegexOptions.CultureInvariant | RegexOptions.Compiled);

    // Step-definition attribute (canonical name, without the "Attribute" suffix) -> the
    // scenario blocks it registers. [StepDefinition] registers for Given, When and Then.
    private static readonly IReadOnlyDictionary<string, ScenarioBlock[]> StepDefinitionAttributes =
        new Dictionary<string, ScenarioBlock[]>(StringComparer.Ordinal)
        {
            ["Given"] = new[] { ScenarioBlock.Given },
            ["When"] = new[] { ScenarioBlock.When },
            ["Then"] = new[] { ScenarioBlock.Then },
            ["StepDefinition"] = new[] { ScenarioBlock.Given, ScenarioBlock.When, ScenarioBlock.Then }
        };

    // Hook attribute (canonical name, without the "Attribute" suffix) -> hook type.
    // [Before]/[After] are synonyms for [BeforeScenario]/[AfterScenario].
    private static readonly IReadOnlyDictionary<string, HookType> HookAttributes =
        new Dictionary<string, HookType>(StringComparer.Ordinal)
        {
            ["BeforeTestRun"] = HookType.BeforeTestRun,
            ["AfterTestRun"] = HookType.AfterTestRun,
            ["BeforeFeature"] = HookType.BeforeFeature,
            ["AfterFeature"] = HookType.AfterFeature,
            ["BeforeScenario"] = HookType.BeforeScenario,
            ["Before"] = HookType.BeforeScenario,
            ["AfterScenario"] = HookType.AfterScenario,
            ["After"] = HookType.AfterScenario,
            ["BeforeScenarioBlock"] = HookType.BeforeScenarioBlock,
            ["AfterScenarioBlock"] = HookType.AfterScenarioBlock,
            ["BeforeStep"] = HookType.BeforeStep,
            ["AfterStep"] = HookType.AfterStep
        };

    /// <summary>
    /// Discovers only the step-definition bindings in the file. Retained for backwards
    /// compatibility; prefer <see cref="ParseBindings"/> to also obtain hooks.
    /// </summary>
    public async Task<List<ProjectStepDefinitionBinding>> Parse(CSharpStepDefinitionFile stepDefinitionFile)
    {
        var bindings = await ParseBindings(stepDefinitionFile);
        return bindings.StepDefinitions.ToList();
    }

    /// <summary>
    /// Discovers all bindings (step definitions and hooks) declared in the file.
    /// </summary>
    public async Task<StepDefinitionFileBindings> ParseBindings(CSharpStepDefinitionFile stepDefinitionFile)
    {
        var rootNode = await stepDefinitionFile.Content.GetRootAsync();

        var stepDefinitions = new List<ProjectStepDefinitionBinding>();
        var hooks = new List<ProjectHookBinding>();

        var allMethods = rootNode
            .DescendantNodes()
            .OfType<MethodDeclarationSyntax>();

        foreach (var method in allMethods)
        {
            // The [Scope] attribute can be applied to the containing type and/or the method;
            // the two combine. We resolve it once per method and share it across the method's
            // step/hook attributes.
            var typeScope = ReadScopeAttributes(GetContainingTypeAttributeLists(method));
            var methodScope = ReadScopeAttributes(method.AttributeLists);
            var combinedScope = CombineScopes(typeScope, methodScope);

            var sourceLocation = GetSourceLocation(stepDefinitionFile.FullName, method);
            var parameterTypes = method.ParameterList.Parameters
                .Where(p => p.Type != null)
                .Select(p => p.Type.ToString())
                .ToArray();
            var implementation =
                new ProjectBindingImplementation(FullMethodName(method), parameterTypes, sourceLocation);

            foreach (var attribute in EnumerateAttributes(method.AttributeLists))
            {
                var attributeName = GetAttributeName(attribute);

                if (StepDefinitionAttributes.TryGetValue(attributeName, out var blocks))
                    AddStepDefinitions(stepDefinitions, attribute, blocks, combinedScope, implementation);
                else if (HookAttributes.TryGetValue(attributeName, out var hookType))
                    hooks.Add(CreateHook(attribute, hookType, combinedScope, implementation));
            }
        }

        return new StepDefinitionFileBindings(stepDefinitions, hooks);
    }

    private static void AddStepDefinitions(List<ProjectStepDefinitionBinding> target, AttributeSyntax attribute,
        ScenarioBlock[] blocks, RawScope scope, ProjectBindingImplementation implementation)
    {
        var expression = GetStepDefinitionExpression(attribute);
        // A regex-less binding (e.g. method-name style, or an attribute with no literal
        // expression yet while typing) is still recorded so usage-based features can see it;
        // it simply will not match any step until the expression is known.
        var regex = expression == null ? null : BuildRegex(expression);

        foreach (var block in blocks)
            target.Add(new ProjectStepDefinitionBinding(block, regex, BuildScope(scope), implementation, expression));
    }

    /// <summary>
    /// Converts a Reqnroll step-definition expression to a compiled <see cref="Regex"/>. The
    /// expression may be a plain regex or a Cucumber expression that uses <c>{paramType}</c>
    /// placeholders. Standard Cucumber parameter types are converted to their canonical regex
    /// patterns; unknown/custom types (e.g. project-defined step-argument transformations like
    /// <c>{Verb}</c>) fall back to <c>(.*)</c> so the binding can still match its steps even
    /// though the precise regex is not statically derivable without a semantic model.
    /// For plain-regex expressions the substitution is a no-op (no <c>{...}</c> present).
    /// </summary>
    private static Regex BuildRegex(string expression)
    {
        var regexBody = CucumberParamPattern.Replace(expression, m =>
            m.Groups[1].Value switch
            {
                "int" or "byte" or "short" or "long"    => @"(-?\d+)",
                "float" or "double"                      => @"(-?\d*(?:\.\d+)?)",
                "biginteger" or "bigdecimal"             => @"(-?\d+(?:\.\d+)?)",
                "word"                                   => @"(\w+)",
                // {string} matches a double- or single-quoted literal (simplified pattern).
                "string"                                 => @"(""[^""]*""|'[^']*')",
                // Custom/unknown parameter types (e.g. step-argument transformations): fall
                // back to (.*) — the same pattern the connector produces for them at runtime.
                _                                        => @"(.*)"
            });
        return new Regex($"^{regexBody}$", RegexOptions.CultureInvariant);
    }

    private static ProjectHookBinding CreateHook(AttributeSyntax attribute, HookType hookType, RawScope methodScope,
        ProjectBindingImplementation implementation)
    {
        var hookTags = GetHookTags(attribute);
        var order = GetHookOrder(attribute);
        var scope = BuildScope(CombineWithHookTags(methodScope, hookTags));
        return new ProjectHookBinding(implementation, scope, hookType, order, null);
    }

    private static IEnumerable<AttributeSyntax> EnumerateAttributes(SyntaxList<AttributeListSyntax> attributeLists) =>
        attributeLists.SelectMany(al => al.Attributes);

    private static SyntaxList<AttributeListSyntax> GetContainingTypeAttributeLists(MethodDeclarationSyntax method) =>
        method.Parent is TypeDeclarationSyntax type ? type.AttributeLists : default;

    /// <summary>
    /// Returns the attribute's simple name with any namespace qualification and the
    /// conventional <c>Attribute</c> suffix removed (e.g. <c>Reqnroll.GivenAttribute</c> -&gt; <c>Given</c>).
    /// </summary>
    private static string GetAttributeName(AttributeSyntax attribute)
    {
        var name = attribute.Name;
        while (name is QualifiedNameSyntax qualified)
            name = qualified.Right;
        if (name is AliasQualifiedNameSyntax aliasQualified)
            name = aliasQualified.Name;

        var text = (name as SimpleNameSyntax)?.Identifier.Text ?? name.ToString();
        if (text.Length > AttributeSuffix.Length && text.EndsWith(AttributeSuffix, StringComparison.Ordinal))
            text = text.Substring(0, text.Length - AttributeSuffix.Length);
        return text;
    }

    /// <summary>
    /// Extracts the step-text expression: the explicit <c>Expression = "..."</c> named argument
    /// if present, otherwise the first positional string-literal argument. Returns <c>null</c>
    /// when no literal expression can be determined statically.
    /// </summary>
    private static string GetStepDefinitionExpression(AttributeSyntax attribute)
    {
        if (attribute.ArgumentList == null)
            return null;

        var arguments = attribute.ArgumentList.Arguments;

        var named = arguments.FirstOrDefault(a => a.NameEquals?.Name.Identifier.Text == "Expression");
        if (named != null)
            return GetStringConstant(named.Expression);

        var positional = arguments.FirstOrDefault(a => a.NameEquals == null);
        return GetStringConstant(positional?.Expression);
    }

    private static IReadOnlyList<string> GetHookTags(AttributeSyntax attribute)
    {
        if (attribute.ArgumentList == null)
            return Array.Empty<string>();

        return attribute.ArgumentList.Arguments
            .Where(a => a.NameEquals == null)
            .Select(a => GetStringConstant(a.Expression))
            .Where(t => !string.IsNullOrWhiteSpace(t))
            .ToArray();
    }

    private static int? GetHookOrder(AttributeSyntax attribute)
    {
        var orderArgument = attribute.ArgumentList?.Arguments
            .FirstOrDefault(a => a.NameEquals?.Name.Identifier.Text == "Order");
        if (orderArgument?.Expression is LiteralExpressionSyntax literal && literal.Token.Value is int order)
            return order;
        return null;
    }

    /// <summary>
    /// Returns the compile-time string value of a string-literal expression (regular or verbatim),
    /// or <c>null</c> for anything that cannot be evaluated by syntax alone (nameof, concatenation, etc.).
    /// </summary>
    private static string GetStringConstant(ExpressionSyntax expression)
    {
        if (expression is LiteralExpressionSyntax literal && literal.IsKind(SyntaxKind.StringLiteralExpression))
            return literal.Token.ValueText;
        return null;
    }

    private static string FullMethodName(MethodDeclarationSyntax method)
    {
        var segments = new List<string>();
        for (var parent = method.Parent; parent != null; parent = parent.Parent)
        {
            switch (parent)
            {
                case TypeDeclarationSyntax type:
                    segments.Insert(0, type.Identifier.Text);
                    break;
                case BaseNamespaceDeclarationSyntax ns:
                    segments.Insert(0, ns.Name.ToString());
                    break;
            }
        }

        var sb = new StringBuilder();
        foreach (var segment in segments)
            sb.Append(segment).Append('.');
        sb.Append(method.Identifier.Text);
        return sb.ToString();
    }

    private static SourceLocation GetSourceLocation(string sourceFile, MethodDeclarationSyntax method)
    {
        // Point to the method identifier (name token) — consistent with standard LSP convention
        // where textDocument/definition returns the span of the symbol being defined, not the
        // full declaration.  This produces a zero-width highlight at the method name regardless
        // of whether the method has a block body, expression body, or attributes.
        var pos = method.Identifier.GetLocation().GetLineSpan().StartLinePosition;
        return new SourceLocation(sourceFile,
            pos.Line + 1, pos.Character + 1,
            pos.Line + 1, pos.Character + 1);
    }

    private static RawScope ReadScopeAttributes(SyntaxList<AttributeListSyntax> attributeLists)
    {
        RawScope result = null;
        foreach (var attribute in EnumerateAttributes(attributeLists))
        {
            if (GetAttributeName(attribute) != "Scope")
                continue;

            var raw = ReadScopeAttribute(attribute);
            // Multiple [Scope] attributes at the same level broaden the match (OR semantics).
            result = result == null ? raw : OrScopes(result, raw);
        }

        return result;
    }

    private static RawScope ReadScopeAttribute(AttributeSyntax attribute)
    {
        string tag = null, feature = null, scenario = null;
        if (attribute.ArgumentList != null)
        {
            foreach (var argument in attribute.ArgumentList.Arguments)
            {
                switch (argument.NameEquals?.Name.Identifier.Text)
                {
                    case "Tag":
                        tag = GetStringConstant(argument.Expression);
                        break;
                    case "Feature":
                        feature = GetStringConstant(argument.Expression);
                        break;
                    case "Scenario":
                        scenario = GetStringConstant(argument.Expression);
                        break;
                }
            }
        }

        return new RawScope(tag, feature, scenario);
    }

    private static RawScope OrScopes(RawScope first, RawScope second) =>
        new(
            CombineTags(first.Tag, second.Tag, " or "),
            first.Feature ?? second.Feature,
            first.Scenario ?? second.Scenario);

    /// <summary>
    /// Combines the type-level and method-level [Scope] attributes. A binding is in scope only
    /// when both levels match, so their tags are AND-combined; the more specific method-level
    /// feature/scenario title wins.
    /// </summary>
    private static RawScope CombineScopes(RawScope typeScope, RawScope methodScope)
    {
        if (typeScope == null)
            return methodScope;
        if (methodScope == null)
            return typeScope;

        return new RawScope(
            CombineTags(typeScope.Tag, methodScope.Tag, " and "),
            methodScope.Feature ?? typeScope.Feature,
            methodScope.Scenario ?? typeScope.Scenario);
    }

    /// <summary>
    /// A hook applies when the scenario is within the method/type scope and carries one of the
    /// tags listed on the hook attribute. The hook's tags are OR-combined and then AND-combined
    /// with the surrounding [Scope].
    /// </summary>
    private static RawScope CombineWithHookTags(RawScope methodScope, IReadOnlyList<string> hookTags)
    {
        if (hookTags.Count == 0)
            return methodScope;

        var tagsExpression = string.Join(" or ", hookTags.Select(NormalizeTag));
        var hookScope = new RawScope(tagsExpression, null, null);
        return CombineScopes(methodScope, hookScope);
    }

    private static string CombineTags(string left, string right, string op)
    {
        if (string.IsNullOrWhiteSpace(left))
            return right;
        if (string.IsNullOrWhiteSpace(right))
            return left;
        return $"({left}){op}({right})";
    }

    private static string NormalizeTag(string tag) =>
        tag.StartsWith("@", StringComparison.Ordinal) ? tag : "@" + tag;

    private static Scope BuildScope(RawScope raw)
    {
        if (raw == null || (string.IsNullOrWhiteSpace(raw.Tag) && raw.Feature == null && raw.Scenario == null))
            return null;

        ITagExpression tagExpression = null;
        string error = null;
        if (!string.IsNullOrWhiteSpace(raw.Tag))
        {
            var parsed = TagExpressionParser.Parse(raw.Tag);
            if (parsed is InvalidTagExpression invalid)
                error = $"Invalid tag expression '{raw.Tag}': {invalid.Message}";
            else
                tagExpression = parsed;
        }

        return new Scope
        {
            Tag = tagExpression,
            FeatureTitle = raw.Feature,
            ScenarioTitle = raw.Scenario,
            Error = error
        };
    }

    private sealed record RawScope(string Tag, string Feature, string Scenario);

#nullable enable
    /// <summary>
    /// Information about a string literal token in a binding attribute argument — its source
    /// span, syntax kind (regular string or raw string), and raw source text.
    /// </summary>
    public sealed record AttributeStringInfo(
        TextSpan Span,
        SyntaxKind LiteralKind,
        string RawText);

    /// <summary>
    /// Locates the string-literal argument of a binding attribute at the given source position
    /// and attribute index, and returns information about its token. Returns <c>null</c> when
    /// the argument is not a literal string expression (e.g. a constant reference or nameof).
    /// </summary>
    public async Task<AttributeStringInfo?> GetAttributeStringInfo(
        CSharpStepDefinitionFile file,
        int methodLine,
        int methodColumn,
        int attributeIndex,
        string expression)
    {
        var rootNode = await file.Content.GetRootAsync();

        var methods = rootNode
            .DescendantNodes()
            .OfType<MethodDeclarationSyntax>();

        foreach (var method in methods)
        {
            var pos = method.Identifier.GetLocation().GetLineSpan().StartLinePosition;
            if (pos.Line != methodLine - 1)
                continue;
            // Column check is intentionally omitted: the connector stores the method
            // declaration start column, not the method identifier position, so an exact
            // match would fail. The caller already disambiguates via attributeIndex.

            var attributes = EnumerateAttributes(method.AttributeLists).ToList();
            if (attributeIndex < 0 || attributeIndex >= attributes.Count)
                return null;

            var attribute = attributes[attributeIndex];
            if (attribute.ArgumentList == null)
                return null;

            var literalArgument = attribute.ArgumentList.Arguments
                .Select(a => a.Expression)
                .OfType<LiteralExpressionSyntax>()
                .FirstOrDefault(e => e.IsKind(SyntaxKind.StringLiteralExpression));

            if (literalArgument == null)
                return null;

            return new AttributeStringInfo(
                literalArgument.Token.Span,
                literalArgument.Token.Kind(),
                literalArgument.Token.Text);
        }

        return null;
    }
}
