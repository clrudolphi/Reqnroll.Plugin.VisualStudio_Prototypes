#nullable enable

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Extensibility;
using Microsoft.VisualStudio.Extensibility.Commands;
using Microsoft.VisualStudio.Extensibility.Editor;
using Microsoft.VisualStudio.Shell;
using Newtonsoft.Json.Linq;
using Reqnroll.IdeSupport.Common.Diagnostics;
using Reqnroll.IdeSupport.VisualStudio.Extension.Navigation;

namespace Reqnroll.IdeSupport.VisualStudio.Extension.RenameStep;

/// <summary>
/// \"Rename Step\" command for the Visual Studio extension (F16 Step Rename Refactoring).
/// <para>
/// When invoked from a C# binding file, queries the server for renameable targets,
/// shows a picker if multiple targets exist, selects the target, prompts the user
/// for new step text, and sends the rename directly over the LSP pipe.
/// </para>
/// </summary>
[VisualStudioContribution]
internal sealed class RenameStepCommand : Command
{
    private const string RenameMethod = "textDocument/rename";

    private readonly RenameStepState _state;
    private readonly TraceSource _traceSource;
    private readonly IDeveroomLogger _fileLogger = new SynchronousFileLogger();

    private static readonly Guid GuidSHLMainMenu = new("{D309F791-903F-11D0-9EFC-00A0C911004F}");
    private const int IDG_VS_CODEWIN_NAVIGATETOLOCATION = 0x02B1;

    public RenameStepCommand(RenameStepState state, TraceSource traceSource)
    {
        _state = state;
        _traceSource = traceSource;
    }

    public override CommandConfiguration CommandConfiguration => new("Rename Step")
    {
        Icon = new CommandIconConfiguration(ImageMoniker.Custom("ReqnrollIcon"), IconSettings.IconAndText),
        VisibleWhen = ActivationConstraint.Or(
            ActivationConstraint.EditorContentType("CSharp"),
            ActivationConstraint.EditorContentType("reqnroll-gherkin")),
        Placements =
        [
            CommandPlacement.VsctParent(GuidSHLMainMenu, id: IDG_VS_CODEWIN_NAVIGATETOLOCATION, priority: 0x0100),
        ],
    };

    public override async Task ExecuteCommandAsync(IClientContext context, CancellationToken cancellationToken)
    {
        try
        {
            _fileLogger.LogInfo("RenameStepCommand: invoked.");

            var service = _state.Service;
            if (service is null)
            {
                _fileLogger.LogWarning("RenameStepCommand: LSP server not yet initialized.");
                VsUtils.ShowStatusBarMessage("Reqnroll: LSP server not yet initialized — open a .feature file to activate it.");
                return;
            }

            var textView = await context.GetActiveTextViewAsync(cancellationToken).ConfigureAwait(false);
            if (textView is null)
            {
                _fileLogger.LogWarning("RenameStepCommand: No active text view in client context.");
                return;
            }

            var fileUri  = textView.Uri.ToString();
            var caretPos = textView.Selection.ActivePosition;
            var line     = caretPos.GetContainingLine();
            var lineNum  = line.LineNumber;
            var charNum  = caretPos.Offset - line.Text.Start;

            _fileLogger.LogInfo(
                $"RenameStepCommand: active view uri='{fileUri}', caret line={lineNum} char={charNum}.");

            // ── Step 1: Get rename targets from the server ───────────────────
            var targets = await service.GetRenameTargetsAsync(fileUri, lineNum, charNum, cancellationToken)
                .ConfigureAwait(false);

            if (targets is null)
            {
                _fileLogger.LogInfo("RenameStepCommand: no renameable targets at cursor position.");
                VsUtils.ShowStatusBarMessage("Reqnroll: No step definition found to rename at this position.");
                return;
            }

            var targetsArray = targets["targets"] as JArray;
            if (targetsArray is null || targetsArray.Count == 0)
            {
                _fileLogger.LogInfo("RenameStepCommand: empty targets array.");
                VsUtils.ShowStatusBarMessage("Reqnroll: No step definition found to rename at this position.");
                return;
            }

            // ── Step 2: Select target (picker if multiple) ──────────────────
            int selectedAttributeIndex;
            string currentLabel;
            string currentExpression;

            if (targetsArray.Count == 1)
            {
                selectedAttributeIndex = targetsArray[0]["attributeIndex"]?.Value<int>() ?? 0;
                currentLabel = targetsArray[0]["label"]?.Value<string>() ?? "";
                currentExpression = targetsArray[0]["expression"]?.Value<string>() ?? "";
                _fileLogger.LogInfo($"RenameStepCommand: single target, attributeIndex={selectedAttributeIndex}, label='{currentLabel}'.");
            }
            else
            {
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);

                var pickerTargets = targetsArray
                    .Select((t, i) =>
                    {
                        var label = t["label"]?.Value<string>() ?? $"Step definition {i + 1}";
                        return new NavigationTarget(label, textView.Uri.LocalPath, 0, 0);
                    })
                    .ToList();

                var dialog = new NavigationPickerDialog("Choose step definition to rename", pickerTargets);
                if (dialog.ShowModal() != true || dialog.SelectedIndex < 0)
                {
                    _fileLogger.LogInfo("RenameStepCommand: picker dismissed.");
                    return;
                }

                selectedAttributeIndex = targetsArray[dialog.SelectedIndex]["attributeIndex"]?.Value<int>() ?? 0;
                currentLabel = targetsArray[dialog.SelectedIndex]["label"]?.Value<string>() ?? "";
                currentExpression = targetsArray[dialog.SelectedIndex]["expression"]?.Value<string>() ?? "";
                _fileLogger.LogInfo($"RenameStepCommand: user selected target index={dialog.SelectedIndex}, attributeIndex={selectedAttributeIndex}.");
            }

            // ── Step 3: Tell the server which attribute was selected ────────
            await service.SelectRenameTargetAsync(fileUri, version: 0, selectedAttributeIndex, cancellationToken)
                .ConfigureAwait(false);

            // ── Step 4: Prompt user for new step text ────────────────────────
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);

            // Seed the dialog with the live source expression (preserves Cucumber parameter
            // types such as {int}). Fall back to stripping the step-type prefix off the label
            // for older servers that do not send the "expression" field.
            string currentStepText;
            if (!string.IsNullOrEmpty(currentExpression))
            {
                currentStepText = currentExpression;
            }
            else
            {
                var stepTypePrefix = currentLabel.IndexOf(' ') >= 0
                    ? currentLabel.Substring(0, currentLabel.IndexOf(' ')) + " "
                    : "";
                currentStepText = currentLabel.Length > stepTypePrefix.Length
                    ? currentLabel.Substring(stepTypePrefix.Length)
                    : currentLabel;
            }

            var newStepText = Microsoft.VisualBasic.Interaction.InputBox(
                "Enter the new step text:",
                "Rename Step",
                currentStepText);
            if (string.IsNullOrEmpty(newStepText))
            {
                _fileLogger.LogInfo("RenameStepCommand: user cancelled rename dialog.");
                return;
            }

            _fileLogger.LogInfo($"RenameStepCommand: user entered new text '{newStepText}'.");

            // ── Step 5: Send textDocument/rename via the pipe ──────────────
            var renameParams = BuildRenameParams(fileUri, lineNum, charNum, newStepText);
            _fileLogger.LogInfo($"RenameStepCommand: sending {RenameMethod} with params={renameParams}");

            var pipe = GetPipe(service);
            if (pipe is null)
            {
                _fileLogger.LogWarning("RenameStepCommand: LspInterceptingPipe not available.");
                VsUtils.ShowStatusBarMessage("Reqnroll: Could not access LSP pipe to execute rename.");
                return;
            }

            var result = await pipe
                .SendRequestToServerAsync(RenameMethod, renameParams, cancellationToken)
                .ConfigureAwait(false);

            if (result is null)
            {
                _fileLogger.LogWarning("RenameStepCommand: server returned null from rename.");
                VsUtils.ShowStatusBarMessage("Reqnroll: Rename failed — the step definition could not be renamed.");
                return;
            }

            _fileLogger.LogInfo($"RenameStepCommand: rename result = {result}");

            // ── Step 6: Apply the WorkspaceEdit ────────────────────────────
            await ApplyWorkspaceEditAsync(result, cancellationToken).ConfigureAwait(false);

            _fileLogger.LogInfo("RenameStepCommand: rename completed successfully.");
        }
        catch (Exception ex)
        {
            _fileLogger.LogWarning($"RenameStepCommand: failed: {ex}");
            _traceSource.TraceEvent(TraceEventType.Error, 0, "RenameStepCommand: failed: {0}", ex);
        }
    }

    // ── Helpers ────────────────────────────────────────────────────────────────

    private static string BuildRenameParams(string fileUri, int line0, int char0, string newName)
    {
        var escapedUri = Newtonsoft.Json.JsonConvert.ToString(fileUri);
        var escapedNewName = Newtonsoft.Json.JsonConvert.ToString(newName);
        return $"{{" +
               $"\"textDocument\":{{\"uri\":{escapedUri}}}," +
               $"\"position\":{{\"line\":{line0},\"character\":{char0}}}," +
               $"\"newName\":{escapedNewName}" +
               $"}}";
    }

    private static LspInterception.LspInterceptingPipe? GetPipe(RenameStepService service)
    {
        // The pipe is accessed via a field on the service. Since we can't inject the pipe
        // directly into the command (it's created after server init), the service holds it.
        // Use reflection to get the private _pipe field.
        var field = typeof(RenameStepService).GetField("_pipe",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        return field?.GetValue(service) as LspInterception.LspInterceptingPipe;
    }

    private async Task ApplyWorkspaceEditAsync(JToken result, CancellationToken cancellationToken)
    {
        if (result is not JObject editObj)
        {
            _fileLogger.LogWarning("RenameStepCommand: rename result is not a JSON object.");
            return;
        }

        var changes = editObj["changes"] as JObject;
        if (changes is null)
        {
            _fileLogger.LogWarning("RenameStepCommand: rename result has no 'changes' property.");
            return;
        }

        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);

        foreach (var fileEntry in changes)
        {
            var uri = fileEntry.Key;
            var edits = fileEntry.Value as JArray;
            if (edits is null || edits.Count == 0)
                continue;

            // Convert file URI to local path
            var localPath = uri;
            if (localPath.StartsWith("file:///", StringComparison.OrdinalIgnoreCase))
                localPath = localPath.Substring(8).Replace('/', '\\');

            _fileLogger.LogInfo($"RenameStepCommand: applying {edits.Count} edit(s) to '{localPath}'.");

            // Read the file
            if (!System.IO.File.Exists(localPath))
            {
                _fileLogger.LogWarning($"RenameStepCommand: file not found: '{localPath}'.");
                continue;
            }

            var text = System.IO.File.ReadAllText(localPath);
            var lines = text.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None);

            // Apply edits in reverse order (bottom to top so line numbers stay valid)
            var textEdits = new List<(int startLine, int startChar, int endLine, int endChar, string newText)>();
            foreach (var edit in edits.Cast<JObject>())
            {
                var range = edit["range"];
                if (range is null) continue;
                var start = range["start"];
                var end = range["end"];
                if (start is null || end is null) continue;

                textEdits.Add((
                    start["line"]?.Value<int>() ?? 0,
                    start["character"]?.Value<int>() ?? 0,
                    end["line"]?.Value<int>() ?? 0,
                    end["character"]?.Value<int>() ?? 0,
                    edit["newText"]?.Value<string>() ?? ""
                ));
            }

            // Sort by descending start position so edits don't shift each other
            textEdits.Sort((a, b) =>
            {
                var lineCmp = b.startLine.CompareTo(a.startLine);
                return lineCmp != 0 ? lineCmp : b.startChar.CompareTo(a.startChar);
            });

            foreach (var (sl, sc, el, ec, nt) in textEdits)
            {
                if (sl >= lines.Length) continue;

                var currentLine = lines[sl];
                var newLine = currentLine.Substring(0, sc) + nt;
                if (el < lines.Length)
                {
                    var endLine = lines[el];
                    if (ec <= endLine.Length)
                        newLine += endLine.Substring(ec);
                }
                lines[sl] = newLine;

                // Remove lines between start and end (if multi-line edit)
                for (int i = sl + 1; i <= el && i < lines.Length; i++)
                    lines[i] = null!;
            }

            // Remove nulled lines
            var finalLines = lines.Where(l => l != null).ToArray();
            var newContent = string.Join(Environment.NewLine, finalLines);
            System.IO.File.WriteAllText(localPath, newContent);

            _fileLogger.LogInfo($"RenameStepCommand: wrote {finalLines.Length} lines to '{localPath}'.");

            // Notify the LSP server about the change to a .feature file.
            // When a closed feature file is modified by the rename, no didChange fires
            // through VS's normal text document synchronization, leaving the server's
            // in-memory match cache stale.  Send a synthetic didChange notification so
            // that subsequent operations (e.g. Find All References) return fresh data.
            if (localPath.EndsWith(".feature", StringComparison.OrdinalIgnoreCase))
            {
                var didChangePipe = GetPipe(_state.Service!);
                if (didChangePipe is not null)
                {
                    var escapedContent = Newtonsoft.Json.JsonConvert.ToString(newContent);
                    var featureUri = "file:///" + localPath.Replace('\\', '/');
                    var didChangeParams = $"{{\"textDocument\":{{\"uri\":\"{featureUri}\",\"version\":1}},\"contentChanges\":[{{\"text\":{escapedContent}}}]}}";
                    _ = didChangePipe.SendNotificationToServerAsync(
                        "textDocument/didChange",
                        didChangeParams,
                        cancellationToken);
                    _fileLogger.LogInfo($"RenameStepCommand: sent didChange for '{localPath}'.");
                }
            }
        }

        VsUtils.ShowStatusBarMessage("Reqnroll: Step renamed successfully.");
    }
}
