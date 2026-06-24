#nullable enable

using Reqnroll.IdeSupport.Common.Configuration;
using Reqnroll.IdeSupport.LSP.Core.Gherkin.Parsing;

namespace Reqnroll.IdeSupport.LSP.Core.Editor.Scaffolding;

/// <summary>
/// An immutable description of one generated step-definition stub method.
/// Built by <see cref="StepScaffoldService"/> and rendered by <see cref="StepSkeletonRenderer"/>.
/// </summary>
public sealed record StepSkeletonDescriptor(
    ScenarioBlock                  Block,
    SnippetExpressionStyle         Style,
    string                         ExpressionText,
    IReadOnlyList<(string Type, string Name)> Parameters,
    string                         MethodName,
    bool                           IsAsync)
{
    /// <summary>
    /// The normalised deduplication key: keyword + escaped expression.
    /// Two undefined steps that produce the same expression collapse to one skeleton.
    /// </summary>
    public string DeduplicationKey => $"{Block}:{ExpressionText}";
}
