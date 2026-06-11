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
using Reqnroll.IdeSupport.VisualStudio.Extension.GoToHooks;
using Reqnroll.IdeSupport.VisualStudio.Extension.Navigation;

namespace Reqnroll.IdeSupport.VisualStudio.Extension.GoToDefinition;

/// <summary>
/// "Go to Definition" command — placed in the code-editor context menu navigation group,
/// visible only when a <c>.feature</c> file editor is active (design doc F5).
/// </summary>
/// <remarks>
/// <para>
/// When invoked on a step line, queries the LSP server via <c>textDocument/definition</c>:
/// a single result navigates directly; multiple results (ambiguous binding) show a
/// "Go to step definitions" picker via <see cref="NavigationPickerHelper.PickAndNavigateAsync"/>.
/// </para>
/// <para>
/// When invoked on a scenario or feature header (where <c>textDocument/definition</c> returns
/// nothing), falls back to <c>reqnroll/goToHooks</c> and shows a "Go to hooks" picker if any
/// applicable hooks are found.
/// </para>
/// </remarks>
[VisualStudioContribution]
internal sealed class GoToDefinitionCommand : Command
{
    private readonly GoToDefinitionState _definitionState;
    private readonly GoToHooksState      _hooksState;
    private readonly TraceSource         _traceSource;
    private readonly IDeveroomLogger     _fileLogger = new SynchronousFileLogger();

    // guidSHLMainMenu (vsshlids.h) — the VS shell's built-in command set.
    private static readonly Guid GuidSHLMainMenu = new("{D309F791-903F-11D0-9EFC-00A0C911004F}");

    // IDG_VS_CODEWIN_NAVIGATETOLOCATION (vsshlids.h) — navigation group in the code-editor
    // context menu (IDM_VS_CTXT_CODEWIN) that hosts "Go To Definition" / "Find All References".
    private const int IDG_VS_CODEWIN_NAVIGATETOLOCATION = 0x02B1;

    public GoToDefinitionCommand(
        GoToDefinitionState definitionState,
        GoToHooksState      hooksState,
        TraceSource         traceSource)
    {
        _definitionState = definitionState;
        _hooksState      = hooksState;
        _traceSource     = traceSource;
    }

    public override CommandConfiguration CommandConfiguration => new("Go to Definition")
    {
        Icon        = new CommandIconConfiguration(ImageMoniker.Custom("ReqnrollIcon"), IconSettings.IconAndText),

        // Show only when a .feature file editor is active; invisible in all other editors.
        VisibleWhen = ActivationConstraint.EditorContentType("reqnroll-gherkin"),

        // Placed in the navigation group of the code-editor context menu alongside
        // "Find All References".
        Placements  =
        [
            CommandPlacement.VsctParent(GuidSHLMainMenu, IDG_VS_CODEWIN_NAVIGATETOLOCATION, 0x0100),
        ],
    };

    public override async Task ExecuteCommandAsync(IClientContext context, CancellationToken cancellationToken)
    {
        try
        {
            _fileLogger.LogInfo("GoToDefinitionCommand: invoked.");

            var textView = await context.GetActiveTextViewAsync(cancellationToken).ConfigureAwait(false);
            if (textView is null)
            {
                _fileLogger.LogWarning("GoToDefinitionCommand: no active text view.");
                return;
            }

            var fileUri  = textView.Uri.ToString();
            var caretPos = textView.Selection.ActivePosition;
            var line     = caretPos.GetContainingLine();
            var lineNum  = line.LineNumber;
            var charNum  = caretPos.Offset - line.Text.Start;

            _fileLogger.LogInfo(
                $"GoToDefinitionCommand: uri='{fileUri}', caret line={lineNum} char={charNum}.");

            if (await TryNavigateToStepDefinitionAsync(fileUri, lineNum, charNum, cancellationToken)
                    .ConfigureAwait(false))
                return;

            await TryNavigateToHooksAsync(fileUri, lineNum, charNum, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _fileLogger.LogWarning($"GoToDefinitionCommand: failed: {ex}");
            _traceSource.TraceEvent(TraceEventType.Error, 0, "GoToDefinitionCommand: failed: {0}", ex);
        }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private async Task<bool> TryNavigateToStepDefinitionAsync(
        string fileUri, int lineNum, int charNum, CancellationToken cancellationToken)
    {
        var definitionService = _definitionState.Service;
        if (definitionService is null)
        {
            _fileLogger.LogWarning("GoToDefinitionCommand: LSP server not yet initialized.");
            return false;
        }

        var result = await definitionService
            .GoToDefinitionAsync(fileUri, lineNum, charNum, cancellationToken)
            .ConfigureAwait(false);

        if (result.Locations.Count == 0)
        {
            _fileLogger.LogInfo("GoToDefinitionCommand: no step definitions at this position.");
            return false;
        }

        _fileLogger.LogInfo($"GoToDefinitionCommand: {result.Locations.Count} step definition(s) found.");

        var targets = BuildStepDefinitionTargets(result.Locations);
        await NavigationPickerHelper.PickAndNavigateAsync(
                targets,
                _fileLogger,
                promptTitle: "Go to step definitions",
                cancellationToken)
            .ConfigureAwait(false);

        return true;
    }

    private async Task TryNavigateToHooksAsync(
        string fileUri, int lineNum, int charNum, CancellationToken cancellationToken)
    {
        var hooksService = _hooksState.Service;
        if (hooksService is null)
        {
            _fileLogger.LogVerbose("GoToDefinitionCommand: hooks service not available.");
            return;
        }

        var result = await hooksService
            .GoToHooksAsync(fileUri, lineNum, charNum, cancellationToken)
            .ConfigureAwait(false);

        if (result.Hooks.Count == 0)
        {
            _fileLogger.LogInfo("GoToDefinitionCommand: no applicable hooks at this position.");
            return;
        }

        _fileLogger.LogInfo($"GoToDefinitionCommand: {result.Hooks.Count} hook(s) found.");

        var targets = BuildHookTargets(result.Hooks);
        await NavigationPickerHelper.PickAndNavigateAsync(
                targets,
                _fileLogger,
                promptTitle: "Go to hooks",
                cancellationToken)
            .ConfigureAwait(false);
    }

    private static IReadOnlyList<NavigationTarget> BuildStepDefinitionTargets(
        IReadOnlyList<StepDefinitionLocation> locations)
    {
        var targets = new List<NavigationTarget>(locations.Count);
        foreach (var loc in locations)
        {
            if (!Uri.TryCreate(loc.Uri, UriKind.Absolute, out var uri) || !uri.IsFile)
                continue;

            var filePath = uri.LocalPath;
            var fileName = Path.GetFileName(filePath);
            // Display: "[When] CalculatorSteps.AddNumbers (Steps.cs:18)"  (1-based line for readability)
            var label = string.IsNullOrEmpty(loc.StepType)
                ? $"{loc.MethodName} ({fileName}:{loc.StartLine + 1})"
                : $"[{loc.StepType}] {loc.MethodName} ({fileName}:{loc.StartLine + 1})";
            targets.Add(new NavigationTarget(label, filePath, loc.StartLine, loc.StartChar));
        }
        return targets;
    }

    private static IReadOnlyList<NavigationTarget> BuildHookTargets(
        IReadOnlyList<HookLocation> hooks)
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
