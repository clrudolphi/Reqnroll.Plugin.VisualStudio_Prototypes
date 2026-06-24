using Reqnroll.IdeSupport.LSP.Core.Documents;

namespace Reqnroll.IdeSupport.LSP.Server.Document;

public class LspTextSnapshot : IGherkinTextSnapshot
{
    private readonly string _text;
    private readonly Lazy<IReadOnlyList<IGherkinTextSnapshotLine>> _lines;

    public LspTextSnapshot(string uri, int version, string text)
    {
        Uri = uri;
        Version = version;
        _text = text;
        _lines = new Lazy<IReadOnlyList<IGherkinTextSnapshotLine>>(BuildLines);
    }

    public string Uri { get; }
    public int Version { get; }
    public string GetText() => _text;
    public int LineCount => _lines.Value.Count;

    public int Length => _text.Length;

    public IGherkinTextSnapshotLine GetLineFromLineNumber(int lineNumber) =>
        _lines.Value[Math.Min(lineNumber, LineCount - 1)];

    private IReadOnlyList<IGherkinTextSnapshotLine> BuildLines()
    {
        var lines = new List<IGherkinTextSnapshotLine>();
        int lineStart = 0;
        int lineNumber = 0;
        for (int i = 0; i < _text.Length;)
        {
            int lineBreakLen = 0;
            if (_text[i] == '\r')
            {
                if (i + 1 < _text.Length && _text[i + 1] == '\n')
                    lineBreakLen = 2;
                else
                    lineBreakLen = 1;
            }
            else if (_text[i] == '\n')
            {
                lineBreakLen = 1;
            }

            if (lineBreakLen > 0)
            {
                int length = i - lineStart;
                lines.Add(new LspTextSnapshotLine(this, lineNumber++, lineStart, i));
                i += lineBreakLen;
                lineStart = i;
            }
            else
            {
                i++;
            }
        }

        // Add last line if text doesn't end with a line break
        if (lineStart <= _text.Length)
        {
            int length = _text.Length - lineStart;
            lines.Add(new LspTextSnapshotLine(this, lineNumber, lineStart, _text.Length));
        }

        return lines;
    }
}

public class LspTextSnapshotLine : IGherkinTextSnapshotLine
{
    private readonly IGherkinTextSnapshot _snapshot;
    private readonly int _start;
    private readonly int _end;

    public LspTextSnapshotLine(IGherkinTextSnapshot snapshot, int lineNumber, int start, int end)
    {
        _snapshot = snapshot;
        _start = start;
        _end = end;
        LineNumber = lineNumber;
    }

    public int LineNumber { get; }
    public int Start => _start;
    public int End => _end;
    public string GetText() => _snapshot.GetText().Substring(_start, _end - _start);
}