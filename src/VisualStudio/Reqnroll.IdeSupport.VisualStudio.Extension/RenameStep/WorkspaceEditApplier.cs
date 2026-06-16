#nullable enable

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Microsoft.VisualStudio.TextManager.Interop;
using Reqnroll.IdeSupport.Common.Diagnostics;

namespace Reqnroll.IdeSupport.VisualStudio.Extension.RenameStep;

/// <summary>
/// Applies a <c>WorkspaceEdit</c> (<c>textDocument/rename</c> result) to files on disk or to
/// open VS text buffers.  For documents open in the editor, edits are applied via
/// <see cref="IVsTextLines.ReplaceLines"/> so that unsaved changes are not overwritten.
/// For closed documents, <c>File.WriteAllText</c> is used as a fallback.
/// </summary>
internal sealed class WorkspaceEditApplier
{
    private readonly RenameStepService _service;
    private readonly IDeveroomLogger   _logger;
    private readonly TraceSource       _traceSource;

    public WorkspaceEditApplier(
        RenameStepService service,
        IDeveroomLogger   logger,
        TraceSource       traceSource)
    {
        _service     = service;
        _logger      = logger;
        _traceSource = traceSource;
    }

    /// <summary>
    /// Applies all file edits from a <see cref="RenameWorkspaceEdit"/> result.
    /// For documents open in the editor, edits are applied via VS text buffer
    /// (<see cref="IVsTextLines.ReplaceLines"/>) so that unsaved changes are not
    /// overwritten.  For closed documents, <c>File.WriteAllText</c> is used.
    /// </summary>
    /// <param name="workspace">The structured workspace edit with pre-parsed file edits.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task ApplyAsync(RenameWorkspaceEdit workspace, CancellationToken cancellationToken)
    {
        if (workspace.FileEdits.Count == 0)
        {
            _logger.LogWarning("WorkspaceEditApplier: no file edits to apply.");
            return;
        }

        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);

        var rdt = ServiceProvider.GlobalProvider.GetService(typeof(SVsRunningDocumentTable))
            as IVsRunningDocumentTable;

        foreach (var kvp in workspace.FileEdits)
        {
            var localPath = kvp.Key;
            var textEdits = kvp.Value;

            // Try VS text buffer first (preserves unsaved changes)
            if (rdt != null && TryApplyToBuffer(rdt, localPath, textEdits, cancellationToken))
                continue;

            // Fall back to File.WriteAllText for closed documents
            ApplyToDisk(localPath, textEdits, cancellationToken);
        }
    }

    /// <summary>
    /// Attempts to apply edits to an open VS document via <see cref="IVsTextLines.ReplaceLines"/>.
    /// Returns <c>true</c> when edits were applied to the buffer.
    /// </summary>
    private bool TryApplyToBuffer(
        IVsRunningDocumentTable rdt,
        string                  localPath,
        List<TextEditItem>      textEdits,
        CancellationToken       cancellationToken)
    {
        var hr = rdt.FindAndLockDocument(
            1, // RDT_NoLock
            localPath,
            out _,
            out _,
            out var docDataPtr,
            out _);

        if (hr != 0 || docDataPtr == IntPtr.Zero)
            return false;

        try
        {
            var docObj = Marshal.GetObjectForIUnknown(docDataPtr);
            if (docObj is not IVsTextLines textLines)
                return false;

            foreach (var edit in textEdits)
            {
                ApplyEditToBuffer(textLines, edit);
            }

            _logger.LogInfo($"WorkspaceEditApplier: applied edits via text buffer for '{localPath}'.");
            NotifyDidChange(localPath, ReadBufferText(textLines), cancellationToken);
            return true;
        }
        finally
        {
            Marshal.Release(docDataPtr);
        }
    }

    /// <summary>
    /// Applies edits by reading from and writing to disk. Used for closed documents.
    /// </summary>
    private void ApplyToDisk(
        string             localPath,
        List<TextEditItem> textEdits,
        CancellationToken  cancellationToken)
    {
        var fileText = System.IO.File.ReadAllText(localPath);
        var lines    = fileText.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None);

        foreach (var edit in textEdits)
        {
            if (edit.StartLine >= lines.Length) continue;

            var currentLine = lines[edit.StartLine];
            var newLine     = currentLine.Substring(0, edit.StartChar) + edit.NewText;

            if (edit.EndLine < lines.Length)
            {
                var endLine = lines[edit.EndLine];
                if (edit.EndChar <= endLine.Length)
                    newLine += endLine.Substring(edit.EndChar);
            }

            lines[edit.StartLine] = newLine;

            for (int i = edit.StartLine + 1; i <= edit.EndLine && i < lines.Length; i++)
                lines[i] = null!;
        }

        var resultLines = lines.Where(l => l != null).ToArray();
        var newContent  = string.Join(Environment.NewLine, resultLines);
        System.IO.File.WriteAllText(localPath, newContent);

        _logger.LogInfo($"WorkspaceEditApplier: wrote {resultLines.Length} lines to '{localPath}'.");
        NotifyDidChange(localPath, newContent, cancellationToken);
    }

    private void NotifyDidChange(string localPath, string? newContent, CancellationToken cancellationToken)
    {
        if (newContent is null || !localPath.EndsWith(".feature", StringComparison.OrdinalIgnoreCase))
            return;

        _ = _service.SendDidChangeAsync(localPath, newContent, cancellationToken);
        _logger.LogInfo($"WorkspaceEditApplier: sent didChange for '{localPath}'.");
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    /// <summary>Applies a single <see cref="TextEditItem"/> to a VS text buffer.</summary>
    private static void ApplyEditToBuffer(IVsTextLines textLines, TextEditItem edit)
    {
        var pszText = Marshal.StringToCoTaskMemUni(edit.NewText);
        try
        {
            var editHr = textLines.ReplaceLines(
                edit.StartLine, edit.StartChar,
                edit.EndLine,   edit.EndChar,
                pszText,
                edit.NewText.Length,
                null);

            if (editHr != 0)
                Trace.WriteLine(
                    $"WorkspaceEditApplier: ReplaceLines failed (hr=0x{editHr:X8}) " +
                    $"at ({edit.StartLine},{edit.StartChar})-({edit.EndLine},{edit.EndChar})");
        }
        finally
        {
            Marshal.FreeCoTaskMem(pszText);
        }
    }

    /// <summary>Reads the full text content of a VS text buffer.</summary>
    private static string? ReadBufferText(IVsTextLines textLines)
    {
        try
        {
            var hr = textLines.GetLineCount(out var lineCount);
            if (hr != 0 || lineCount <= 0) return null;

            var sb = new StringBuilder();
            for (int i = 0; i < lineCount; i++)
            {
                hr = textLines.GetLengthOfLine(i, out var length);
                if (hr != 0) continue;

                hr = textLines.GetLineText(i, 0, i, length, out var lineText);
                if (hr == 0 && lineText is not null)
                {
                    sb.Append(lineText);
                    if (i < lineCount - 1)
                        sb.Append('\n');
                }
            }
            return sb.ToString();
        }
        catch
        {
            return null;
        }
    }
}
