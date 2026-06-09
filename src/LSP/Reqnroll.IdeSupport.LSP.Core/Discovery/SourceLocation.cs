#nullable disable
namespace Reqnroll.IdeSupport.LSP.Core.Discovery;

public class SourceLocation
{
    public SourceLocation(string sourceFile, int sourceFileLine, int sourceFileColumn, int? sourceFileEndLine = null,
        int? sourceFileEndColumn = null)
    {
        SourceFile = sourceFile;
        SourceFileLine = sourceFileLine;
        SourceFileColumn = sourceFileColumn;
        SourceFileEndLine = sourceFileEndLine;
        SourceFileEndColumn = sourceFileEndColumn;
    }

    public string SourceFile { get; }
    public int SourceFileLine { get; } // 1-based
    public int SourceFileColumn { get; } // 1-based
    public int? SourceFileEndLine { get; } // 1-based
    public int? SourceFileEndColumn { get; } // 1-based

    public bool HasEndPosition => SourceFileEndLine != null && SourceFileEndColumn != null;

    /// <summary>Returns <see langword="true"/> when <paramref name="line1Based"/> falls within
    /// the span [<see cref="SourceFileLine"/>, <see cref="SourceFileEndLine"/>].</summary>
    public bool ContainsLine(int line1Based)
    {
        var endLine = SourceFileEndLine ?? SourceFileLine;
        return line1Based >= SourceFileLine && line1Based <= endLine;
    }

    public override string ToString() => $"{SourceFile}({SourceFileLine},{SourceFileColumn})";
}
