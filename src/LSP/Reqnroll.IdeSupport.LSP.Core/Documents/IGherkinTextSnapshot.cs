namespace Reqnroll.IdeSupport.LSP.Core.Documents;

/// <summary>
/// VS-free, immutable, versioned document snapshot.
/// LSP implementation: LspTextSnapshot, constructed from textDocument/didChange content.
/// </summary>
public interface IGherkinTextSnapshot
{
    int Version { get; }
    int LineCount { get; }
    int Length { get; }
    string GetText();
    IGherkinTextSnapshotLine GetLineFromLineNumber(int lineNumber); // 0-based
}

public interface IGherkinTextSnapshotLine
{
    int LineNumber { get; }           // 0-based
    int Start { get; }                // char offset from document start
    int End { get; }                  // incluise of line break chars   
    string GetText();
}