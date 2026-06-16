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

[VisualStudioContribution]
internal sealed class RenameStepCommand : Command
{
    private readonly RenameStepState _state;
    private readonly TraceSource     _traceSource;
    private readonly IDeveroomLogger _fileLogger = new SynchronousFileLogger();

    private static readonly Guid GuidSHLMainMenu = new("{D309F791-903F-11D0-9EFC-00A0C911004F}");
    private const int IDG_VS_CODEWIN_NAVIGATETOLOCATION = 0x02B1;

    public RenameStepCommand(RenameStepState state, TraceSource traceSource)
    {
        _state       = state;
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
                VsUtils.ShowStatusBarMessage("Reqnroll: LSP server not yet initialized.");
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

            _fileLogger.LogInfo($"RenameStepCommand: active view uri='{fileUri}', caret line={lineNum} char={charNum}.");

            // Step 1: Get rename targets from the server
            var targets = await service.GetRenameTargetsAsync(fileUri, lineNum, charNum, cancellationToken)
                .ConfigureAwait(false);

            if (targets is null || targets.Targets.Count == 0)
            {
                _fileLogger.LogInfo("RenameStepCommand: no renameable targets at cursor position.");
                VsUtils.ShowStatusBarMessage("Reqnroll: No step definition found to rename at this position.");
                return;
            }

            // Step 2: Select target (picker if multiple)
            int selectedAttributeIndex;
            string currentLabel;
            string currentExpression;

            if (targets.Targets.Count == 1)
            {
                var item = targets.Targets[0];
                selectedAttributeIndex = item.AttributeIndex;
                currentLabel           = item.Label;
                currentExpression      = item.Expression;
                _fileLogger.LogInfo(
                    $"RenameStepCommand: single target, attributeIndex={selectedAttributeIndex}, label='{currentLabel}'.");
            }
            else
            {
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);

                var pickerTargets = targets.Targets
                    .Select(t => new NavigationTarget(t.Label, textView.Uri.LocalPath, 0, 0))
                    .ToList();

                var dialog = new NavigationPickerDialog("Choose step definition to rename", pickerTargets);
                if (dialog.ShowModal() != true || dialog.SelectedIndex < 0)
                {
                    _fileLogger.LogInfo("RenameStepCommand: picker dismissed.");
                    return;
                }

                var chosen = targets.Targets[dialog.SelectedIndex];
                selectedAttributeIndex = chosen.AttributeIndex;
                currentLabel           = chosen.Label;
                currentExpression      = chosen.Expression;
                _fileLogger.LogInfo(
                    $"RenameStepCommand: user selected target index={dialog.SelectedIndex}, attributeIndex={selectedAttributeIndex}.");
            }

            // Step 3: Tell the server which attribute was selected
            await service.SelectRenameTargetAsync(fileUri, version: 0, selectedAttributeIndex, cancellationToken)
                .ConfigureAwait(false);

            // Step 4: Prompt user for new step text
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);

            var currentStepText = !string.IsNullOrEmpty(currentExpression)
                ? currentExpression
                : ExtractStepTextFromLabel(currentLabel);

            var newStepText = Microsoft.VisualBasic.Interaction.InputBox(
                "Enter the new step text:", "Rename Step", currentStepText);
            if (string.IsNullOrEmpty(newStepText))
            {
                _fileLogger.LogInfo("RenameStepCommand: user cancelled rename dialog.");
                return;
            }

            _fileLogger.LogInfo($"RenameStepCommand: user entered new text '{newStepText}'.");

            // Step 5: Send textDocument/rename via the service
            var result = await service.SendRenameRequestAsync(
                fileUri, lineNum, charNum, newStepText, cancellationToken)
                .ConfigureAwait(false);

            if (result is null)
            {
                _traceSource.TraceInformation("RenameStepCommand: server returned null from rename.");
                VsUtils.ShowStatusBarMessage("Reqnroll: Rename failed.");
                return;
            }

            _fileLogger.LogInfo($"RenameStepCommand: rename result = {result}");

            // Step 6: Apply the WorkspaceEdit (buffer-first for open documents)
            if (result is not null)
            {
                var applier = new WorkspaceEditApplier(service, _fileLogger, _traceSource);
                await applier.ApplyAsync(result, cancellationToken).ConfigureAwait(false);
            }

            _fileLogger.LogInfo("RenameStepCommand: rename completed successfully.");
            VsUtils.ShowStatusBarMessage("Reqnroll: Step renamed successfully.");
        }
        catch (Exception ex)
        {
            _fileLogger.LogWarning($"RenameStepCommand: failed: {ex}");
            _traceSource.TraceEvent(TraceEventType.Error, 0, "RenameStepCommand: failed: {0}", ex);
        }
    }

    private static string ExtractStepTextFromLabel(string label)
    {
        var space = label.IndexOf(' ');
        var prefix = space >= 0 ? label.Substring(0, space + 1) : "";
        return label.Length > prefix.Length ? label.Substring(prefix.Length) : label;
    }
}
