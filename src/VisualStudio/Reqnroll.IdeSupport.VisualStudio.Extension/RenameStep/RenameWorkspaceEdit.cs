#nullable enable

using System.Collections.Generic;
using Newtonsoft.Json.Linq;

namespace Reqnroll.IdeSupport.VisualStudio.Extension.RenameStep;

/// <summary>
/// Client-side result for a <c>textDocument/rename</c> response (F16).
/// Contains pre-parsed, sorted text edits grouped by local file path.
/// </summary>
internal sealed class RenameWorkspaceEdit
{
    /// <summary>
    /// Map from local file path to its sorted list of text edits (bottom-to-top).
    /// </summary>
    public Dictionary<string, List<TextEditItem>> FileEdits { get; } = new();
}

/// <summary>A single position-indexed text replacement within a document.</summary>
internal sealed record TextEditItem(
    int    StartLine,
    int    StartChar,
    int    EndLine,
    int    EndChar,
    string NewText);
