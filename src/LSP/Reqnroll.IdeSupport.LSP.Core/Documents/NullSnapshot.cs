namespace Reqnroll.IdeSupport.LSP.Core.Documents
{
    internal record NullSnapshot : IGherkinTextSnapshot
    {
        public static readonly NullSnapshot Instance = new NullSnapshot();
        public int Version => 0;
        public int LineCount => 0;
        public int Length => 0;
        public string GetText() => string.Empty;
        public IGherkinTextSnapshotLine GetLineFromLineNumber(int lineNumber) => throw new ArgumentOutOfRangeException(nameof(lineNumber), "NullSnapshot has no lines.");
    }
}