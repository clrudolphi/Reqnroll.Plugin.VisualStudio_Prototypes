#nullable enable

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Extensibility;
using Microsoft.VisualStudio.Extensibility.Commands;
using Microsoft.VisualStudio.Extensibility.Editor;
using Reqnroll.IdeSupport.Common.Diagnostics;
using Reqnroll.IdeSupport.VisualStudio.Extension.Navigation;

namespace Reqnroll.IdeSupport.VisualStudio.Extension.GoToHooks;

/// <summary>
/// "Go to Hooks" command — placed in the code-editor context menu navigation group,
/// visible only when a <c>.feature</c> file editor is active (design doc F17).
/// </summary>
/// <remarks>
/// When invoked, queries the LSP server for hook bindings applicable at the caret position.
/// A single result navigates directly; multiple results show a picker via
/// <see cref="NavigationPickerHelper.PickAndNavigateAsync"/>.
/// </remarks>
[VisualStudioContribution]
internal sealed class GoToHooksCommand : Command
{
    private readonly GoToHooksState  _state;
    private readonly TraceSource     _traceSource;
    private readonly IDeveroomLogger _fileLogger = new SynchronousFileLogger();

    // guidSHLMainMenu (vsshlids.h) — the VS shell's built-in command set.
    private static readonly Guid GuidSHLMainMenu = new("{D309F791-903F-11D0-9EFC-00A0C911004F}");

    // IDG_VS_CODEWIN_NAVIGATETOLOCATION (vsshlids.h) — navigation group in the code-editor
    // context menu (IDM_VS_CTXT_CODEWIN) that hosts "Go To Definition" / "Find All References".
    private const int IDG_VS_CODEWIN_NAVIGATETOLOCATION = 0x02B1;

    public GoToHooksCommand(GoToHooksState state, TraceSource traceSource)
    {
        _state       = state;
        _traceSource = traceSource;
    }

    public override CommandConfiguration CommandConfiguration => new("Go to Hooks")
    {
        Icon        = new CommandIconConfiguration(ImageMoniker.Custom("ReqnrollIcon"), IconSettings.IconAndText),

        // Show only when a .feature file editor is active; invisible in all other editors.
        VisibleWhen = ActivationConstraint.EditorContentType("reqnroll-gherkin"),

        // Placed in the navigation group of the code-editor context menu alongside
        // "Go To Definition" and "Find All References".
        Placements  =
        [
            CommandPlacement.VsctParent(GuidSHLMainMenu, IDG_VS_CODEWIN_NAVIGATETOLOCATION, 0x0200),
        ],
    };

    public override async Task ExecuteCommandAsync(IClientContext context, CancellationToken cancellationToken)
    {
        try
        {
            _fileLogger.LogInfo("GoToHooksCommand: invoked.");

            var service = _state.Service;
            if (service is null)
            {
                _fileLogger.LogWarning("GoToHooksCommand: LSP server not yet initialized.");
                return;
            }

            var textView = await context.GetActiveTextViewAsync(cancellationToken).ConfigureAwait(false);
            if (textView is null)
            {
                _fileLogger.LogWarning("GoToHooksCommand: no active text view.");
                return;
            }

            var fileUri  = textView.Uri.ToString();
            var caretPos = textView.Selection.ActivePosition;
            var line     = caretPos.GetContainingLine();
            var lineNum  = line.LineNumber;
            var charNum  = caretPos.Offset - line.Text.Start;

            _fileLogger.LogInfo(
                $"GoToHooksCommand: uri='{fileUri}', caret line={lineNum} char={charNum}.");

            var result = await service
                .GoToHooksAsync(fileUri, lineNum, charNum, cancellationToken)
                .ConfigureAwait(false);

            if (result.Hooks.Count == 0)
            {
                _fileLogger.LogInfo("GoToHooksCommand: no applicable hooks at this position.");
                return;
            }

            _fileLogger.LogInfo($"GoToHooksCommand: {result.Hooks.Count} hook(s) found.");

            var targets = BuildTargets(result.Hooks);
            await NavigationPickerHelper.PickAndNavigateAsync(
                    targets,
                    _fileLogger,
                    promptTitle: "Go to Hook",
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _fileLogger.LogWarning($"GoToHooksCommand: failed: {ex}");
            _traceSource.TraceEvent(TraceEventType.Error, 0, "GoToHooksCommand: failed: {0}", ex);
        }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static IReadOnlyList<NavigationTarget> BuildTargets(IReadOnlyList<HookLocation> hooks)
    {
        var targets = new List<NavigationTarget>(hooks.Count);
        foreach (var h in hooks)
        {
            if (!Uri.TryCreate(h.Uri, UriKind.Absolute, out var uri) || !uri.IsFile)
                continue;

            var filePath    = uri.LocalPath;
            var fileName    = Path.GetFileName(filePath);
            // Display: "[BeforeScenario] SetUpDatabase (Hooks.cs:10)"  (1-based line for readability)
            var displayText = $"[{h.HookType}] {h.MethodName} ({fileName}:{h.StartLine + 1})";
            targets.Add(new NavigationTarget(displayText, filePath, h.StartLine, h.StartChar));
        }
        return targets;
    }
}
