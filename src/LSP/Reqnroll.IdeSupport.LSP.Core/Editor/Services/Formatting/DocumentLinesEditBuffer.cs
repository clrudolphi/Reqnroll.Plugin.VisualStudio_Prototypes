namespace Reqnroll.IdeSupport.LSP.Core.Editor.Services.Formatting;

/// <summary>
/// A line-indexed mutable view of a document (or a sub-range of one), used by
/// <see cref="GherkinDocumentFormatter"/> to apply in-place indentation and
/// table-alignment edits without re-parsing.
/// </summary>
public class DocumentLinesEditBuffer
{
    private readonly string[] _allLines;
    private readonly int _startLine; // zero-based, inclusive
    private readonly int _endLine;   // zero-based, inclusive

    /// <param name="allLines">All document lines (will be mutated in-place for lines within range).</param>
    /// <param name="startLine">Zero-based first line of the editable range (default: 0).</param>
    /// <param name="endLine">Zero-based last line of the editable range (default: last line).</param>
    public DocumentLinesEditBuffer(string[] allLines, int startLine = 0, int? endLine = null)
    {
        _allLines = allLines;
        _startLine = startLine;
        _endLine = endLine ?? (allLines.Length == 0 ? 0 : allLines.Length - 1);
    }

    public bool IsEmpty => _allLines.Length == 0;

    public string GetLineOneBased(int oneBasedLineNumber) => GetLine(oneBasedLineNumber - 1);

    public string GetLine(int zeroBasedLineNumber)
    {
        if (zeroBasedLineNumber < 0 || zeroBasedLineNumber >= _allLines.Length)
            return string.Empty;
        return _allLines[zeroBasedLineNumber];
    }

    public void SetLineOneBased(int oneBasedLineNumber, string line) => SetLine(oneBasedLineNumber - 1, line);

    public void SetLine(int zeroBasedLineNumber, string line)
    {
        if (zeroBasedLineNumber < _startLine || zeroBasedLineNumber > _endLine)
            return;
        if (zeroBasedLineNumber < 0 || zeroBasedLineNumber >= _allLines.Length)
            return;
        _allLines[zeroBasedLineNumber] = line;
    }

    /// <summary>Returns the edited lines within the configured range joined by <paramref name="newLine"/>.</summary>
    public string GetModifiedText(string newLine) => string.Join(newLine, GetEditedLines());

    /// <summary>Returns the lines within the configured edit range.</summary>
    public string[] GetEditedLines()
    {
        if (_allLines.Length == 0) return Array.Empty<string>();
        var count = _endLine - _startLine + 1;
        var result = new string[count];
        Array.Copy(_allLines, _startLine, result, 0, count);
        return result;
    }
}
