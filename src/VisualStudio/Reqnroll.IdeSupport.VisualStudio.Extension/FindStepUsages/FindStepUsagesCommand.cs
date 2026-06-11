#nullable enable

using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Extensibility;
using Microsoft.VisualStudio.Extensibility.Commands;
using Microsoft.VisualStudio.Extensibility.Editor;
using Reqnroll.IdeSupport.Common.Diagnostics;

namespace Reqnroll.IdeSupport.VisualStudio.Extension.FindStepUsages;

/// <summary>
/// Surfaces 1 and 2 — "Find Step Usages" command placed in the Extensions menu and (E6-validated)
/// the C# editor context menu. Extracts the caret position from the active text view, delegates to
/// <see cref="FindStepUsagesService"/>, and renders results via <see cref="FindStepUsagesRenderer"/>.
/// </summary>
[VisualStudioContribution]
internal sealed class FindStepUsagesCommand : Command
{
    private readonly FindStepUsagesState _state;
    private readonly TraceSource _traceSource;
    // VisualStudio.Extensibility's TraceSource is NOT routed to the shared reqnroll-vs-debug-*.log
    // file, so command diagnostics would be invisible there.  Log through the same SynchronousFileLogger
    // the language client and LSP server use so a single run produces a complete diagnostic trail.
    private readonly IDeveroomLogger _fileLogger = new SynchronousFileLogger();

    // Inject only the registered shared-state singleton + SDK TraceSource — both guaranteed
    // resolvable.  Do NOT inject ReqnrollLanguageClient: contribution classes are not documented
    // as injectable into other contributions, and an unresolvable ctor dependency makes the
    // framework fail command construction silently (menu item shows, click does nothing).
    public FindStepUsagesCommand(FindStepUsagesState state, TraceSource traceSource)
    {
        _state = state;
        _traceSource = traceSource;
    }

    // guidSHLMainMenu — the Visual Studio shell's built-in command set (vsshlids.h).
    // VisualStudio.Extensibility's VsctParent can target groups defined by the shell directly,
    // so no custom .vsct / VSSDK command-table registration is required.
    private static readonly Guid GuidSHLMainMenu = new("{D309F791-903F-11D0-9EFC-00A0C911004F}");

    // IDG_VS_CODEWIN_NAVIGATETOLOCATION (vsshlids.h) — the built-in group inside the C# code-editor
    // context menu (IDM_VS_CTXT_CODEWIN) that hosts "Go To Definition" / "Find All References".
    // Parenting here places "Find Step Usages" alongside those navigation commands.
    private const int IDG_VS_CODEWIN_NAVIGATETOLOCATION = 0x02B1;

    public override CommandConfiguration CommandConfiguration => new("Find Step Usages")
    {
        // VS.Extensibility MenuConfiguration has no Icon property, so the icon is carried on the
        // command item itself.  It appears in both placements: the Reqnroll submenu (Surface 1) and
        // the C# editor context menu (Surface 2).
        Icon = new CommandIconConfiguration(ImageMoniker.Custom("ReqnrollIcon"), IconSettings.IconAndText),

        // Show only when a C# file editor is active; invisible in all other editors (including .feature files).
        VisibleWhen = ActivationConstraint.EditorContentType("CSharp"),

        Placements =
        [
            // Surface 1 — child of the Reqnroll submenu in the Extensions menu (ReqnrollMenu.cs).

            // Surface 2 — C# editor context menu, in the built-in navigation group next to
            // "Find All References".  Targets a shell-defined group, so it needs no .vsct file.
            CommandPlacement.VsctParent(
                GuidSHLMainMenu, id: IDG_VS_CODEWIN_NAVIGATETOLOCATION, priority: 0x0100),
        ],
    };

    public override async Task ExecuteCommandAsync(IClientContext context, CancellationToken cancellationToken)
    {
        try
        {
            _fileLogger.LogInfo("FindStepUsagesCommand: invoked.");

            var service  = _state.Service;
            var renderer = _state.Renderer;
            if (service is null || renderer is null)
            {
                _fileLogger.LogWarning(
                    "FindStepUsagesCommand: LSP server not yet initialized " +
                    $"(service={(service is null ? "null" : "set")}, renderer={(renderer is null ? "null" : "set")}).");
                return;
            }

            var textView = await context.GetActiveTextViewAsync(cancellationToken).ConfigureAwait(false);
            if (textView is null)
            {
                _fileLogger.LogWarning("FindStepUsagesCommand: No active text view in client context.");
                return;
            }

            var fileUri  = textView.Uri.ToString();
            var caretPos = textView.Selection.ActivePosition;
            var line     = caretPos.GetContainingLine();
            var lineNum  = line.LineNumber;                 // 0-based, matches LSP convention
            var charNum  = caretPos.Offset - line.Text.Start; // 0-based column

            _fileLogger.LogInfo(
                $"FindStepUsagesCommand: active view uri='{fileUri}', caret line={lineNum} char={charNum}.");

            var result = await service.FindUsagesAsync(fileUri, lineNum, charNum, cancellationToken)
                .ConfigureAwait(false);

            if (!result.IsBinding)
            {
                _fileLogger.LogInfo(
                    $"FindStepUsagesCommand: caret is not on a binding at {fileUri}:{lineNum} — nothing to show.");
                return;
            }

            var count = result.Locations.Count;
            var label = count == 0
                ? "0 usages"
                : $"{count} usage{(count == 1 ? "" : "s")} of step definition";

            _fileLogger.LogInfo(
                $"FindStepUsagesCommand: binding resolved with {count} usage(s); rendering '{label}'.");

            await renderer.RenderAsync(label, result, cancellationToken).ConfigureAwait(false);

            _fileLogger.LogInfo("FindStepUsagesCommand: render complete.");
        }
        catch (Exception ex)
        {
            _fileLogger.LogWarning($"FindStepUsagesCommand: failed: {ex}");
            _traceSource.TraceEvent(TraceEventType.Error, 0, "FindStepUsagesCommand: failed: {0}", ex);
        }
    }
}
