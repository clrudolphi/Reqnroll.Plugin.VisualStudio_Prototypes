using Reqnroll.IdeSupport.LSP.Core.Documents;

namespace Reqnroll.IdeSupport.LSP.Core.Diagnostics;

/// <summary>
/// A protocol-agnostic diagnostic produced for a Gherkin document.
/// The server layer converts this to an LSP <c>Diagnostic</c> before pushing
/// <c>textDocument/publishDiagnostics</c>.
/// </summary>
/// <param name="Message">Human-readable description shown on hover.</param>
/// <param name="Range">The text span to underline in the feature file.</param>
/// <param name="Severity">Error (parse failure) or Warning (unmatched step).</param>
/// <param name="Source">
/// <c>"reqnroll.parser"</c> for parse errors (F4);
/// <c>"reqnroll.binding"</c> for binding mismatches (F3).
/// </param>
public record GherkinDiagnostic(
    string Message,
    GherkinRange Range,
    GherkinDiagnosticSeverity Severity,
    string Source);
