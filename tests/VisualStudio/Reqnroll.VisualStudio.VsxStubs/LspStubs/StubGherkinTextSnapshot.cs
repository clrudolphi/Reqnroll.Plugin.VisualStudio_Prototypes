using Reqnroll.IdeSupport.LSP.Core.Documents;
using System.Collections.Generic;

namespace Reqnroll.VisualStudio.VsxStubs.LspStubs;

/// <summary>
/// Lightweight IGherkinTextSnapshot for use in LSP.Core and LSP.Server tests.
/// Has no VSSDK dependencies.
/// </summary>
public class StubGherkinTextSnapshot : IGherkinTextSnapshot
{
    private readonly string _text;
    private readonly List<IGherkinTextSnapshotLine> _lines;

    public StubGherkinTextSnapshot(string text, int version = 1)
    {
        _text = text;
        Version = version;
        _lines = BuildLines(text);
    }

    public int Version { get; }
    public int LineCount => _lines.Count;
    public int Length => _text.Length;
    public string GetText() => _text;

    public IGherkinTextSnapshotLine GetLineFromLineNumber(int lineNumber) =>
        _lines[System.Math.Min(lineNumber, _lines.Count - 1)];

    private static List<IGherkinTextSnapshotLine> BuildLines(string text)
    {
        var lines = new List<IGherkinTextSnapshotLine>();
        int lineStart = 0;
        int lineNumber = 0;

        for (int i = 0; i < text.Length;)
        {
            int lineBreakLen = 0;
            if (text[i] == '\r')
                lineBreakLen = (i + 1 < text.Length && text[i + 1] == '\n') ? 2 : 1;
            else if (text[i] == '\n')
                lineBreakLen = 1;

            if (lineBreakLen > 0)
            {
                lines.Add(new StubGherkinTextSnapshotLine(text, lineNumber++, lineStart, i));
                i += lineBreakLen;
                lineStart = i;
            }
            else
            {
                i++;
            }
        }

        lines.Add(new StubGherkinTextSnapshotLine(text, lineNumber, lineStart, text.Length));
        return lines;
    }
}

public class StubGherkinTextSnapshotLine : IGherkinTextSnapshotLine
{
    private readonly string _fullText;

    public StubGherkinTextSnapshotLine(string fullText, int lineNumber, int start, int end)
    {
        _fullText = fullText;
        LineNumber = lineNumber;
        Start = start;
        End = end;
    }

    public int LineNumber { get; }
    public int Start { get; }
    public int End { get; }
    public string GetText() => _fullText.Substring(Start, End - Start);
}
