#nullable enable

using System.Collections.Generic;

namespace Reqnroll.IdeSupport.VisualStudio.Extension.FindUnusedStepDefinitions;

/// <summary>One unused step-definition binding, as parsed from the server response.</summary>
internal sealed class UnusedStepLocation
{
    public string? ProjectName       { get; set; }
    public string? ClassName         { get; set; }
    public string? MethodName        { get; set; }
    public string? BindingExpression { get; set; }
    public string? SourceFile        { get; set; }
    public int     SourceLine        { get; set; }  // 0-based
    public int     SourceChar        { get; set; }  // 0-based
}

/// <summary>Parsed result from a <c>reqnroll/findUnusedStepDefinitions</c> response.</summary>
internal sealed class UnusedStepDefinitionsResult
{
    public static readonly UnusedStepDefinitionsResult Empty =
        new(Array.Empty<UnusedStepLocation>());

    public IReadOnlyList<UnusedStepLocation> Items { get; }

    public UnusedStepDefinitionsResult(IReadOnlyList<UnusedStepLocation> items)
        => Items = items;
}
