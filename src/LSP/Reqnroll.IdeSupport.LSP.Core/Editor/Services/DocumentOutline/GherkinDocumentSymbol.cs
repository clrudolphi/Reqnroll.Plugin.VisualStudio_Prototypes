using Reqnroll.IdeSupport.LSP.Core.Documents;

namespace Reqnroll.IdeSupport.LSP.Core.Editor.Services.DocumentOutline;

public enum GherkinSymbolKind
{
    Feature,
    Background,
    Rule,
    Scenario,
    ScenarioOutline,
    Step,
    Examples,
}

public record GherkinDocumentSymbol(
    string Name,
    string? Detail,
    GherkinSymbolKind Kind,
    GherkinRange Range,
    GherkinRange SelectionRange,
    IReadOnlyList<GherkinDocumentSymbol> Children);
