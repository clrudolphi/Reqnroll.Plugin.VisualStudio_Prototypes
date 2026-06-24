namespace Reqnroll.IdeSupport.LSP.Core.Documents;

public class GherkinRange : IEquatable<GherkinRange>
{
    public GherkinRange(IGherkinTextSnapshot snapshot, int start, int length) 
    {
        Snapshot = snapshot ?? throw new ArgumentNullException(nameof(snapshot));
        Start = start;
        Length = length;
    }
    public IGherkinTextSnapshot Snapshot { get; }
    public int Start { get; }
    public int Length { get; }
    public int End => Start + Length;

    // Mirrors SnapshotSpan(startLine.Start, endLine.End) construction pattern
    public static GherkinRange FromLines(
        IGherkinTextSnapshot snapshot,
        IGherkinTextSnapshotLine startLine,
        IGherkinTextSnapshotLine endLine)
    {
        return new GherkinRange(snapshot, startLine.Start, endLine.End - startLine.Start);
    }

    // Mirrors new SnapshotSpan(startPoint, length) construction pattern
    public static GherkinRange FromPoint(
        IGherkinTextSnapshot snapshot, int startOffset, int length)
    {
        if (snapshot is null)
            throw new ArgumentNullException(nameof(snapshot));
        if (startOffset < 0)
            throw new ArgumentOutOfRangeException(nameof(startOffset), startOffset, "Start offset must be non-negative.");
        if (startOffset > snapshot.Length)
            throw new ArgumentOutOfRangeException(nameof(startOffset), startOffset, "Start offset must not exceed the snapshot length.");
        if (length < 0)
            throw new ArgumentOutOfRangeException(nameof(length), length, "Length must be non-negative.");
        if (startOffset + length > snapshot.Length)
            throw new ArgumentOutOfRangeException(nameof(length), length, "The span (startOffset + length) exceeds the snapshot length.");

        return new GherkinRange(snapshot, startOffset, length);
    }

    // Mirrors SnapshotSpan.IntersectsWith
    // Two ranges intersect if they have positions in common, or the end of one
    // coincides with the start of the other — provided neither range is empty.
    public bool IntersectsWith(GherkinRange other)
    {
        if (other is null)
            throw new ArgumentNullException(nameof(other));
        if (!ReferenceEquals(Snapshot, other.Snapshot))
            throw new ArgumentException("Ranges must refer to the same snapshot.", nameof(other));

        // Touching empty spans do NOT count as intersecting (mirrors SnapshotSpan behaviour)
        if (Length == 0 || other.Length == 0)
            return End > other.Start && Start < other.End;

        // Non-empty spans: touching end-to-start counts as intersecting
        return End >= other.Start && Start <= other.End;
    }

    public bool Equals(GherkinRange other)
    {
        if (other is null)
            return false;

        return Start == other.Start
            && Length == other.Length
            && ReferenceEquals(Snapshot, other.Snapshot);
    }

    // Line/character resolution — needed by LSP response mapping
    public (int Line, int Character) StartLinePosition => ResolveOffset(Snapshot, Start);
    public (int Line, int Character) EndLinePosition   => ResolveOffset(Snapshot, End);

    /// <summary>
    /// Converts an absolute character offset to a (line, character) pair using the given snapshot.
    /// Lines and characters are both 0-based (LSP convention).
    /// </summary>
    internal static (int Line, int Character) ResolveOffset(IGherkinTextSnapshot snapshot, int offset)
    {
        for (int ln = 0; ln < snapshot.LineCount; ln++)
        {
            var line = snapshot.GetLineFromLineNumber(ln);
            if (offset <= line.End)
                return (ln, offset - line.Start);
        }
        int lastLine = snapshot.LineCount - 1;
        var last = snapshot.GetLineFromLineNumber(lastLine);
        return (lastLine, last.End - last.Start);
    }

    // Used by VoidDeveroomTag
    public static readonly GherkinRange Empty = new GherkinRange(NullSnapshot.Instance, 0, 0);
}