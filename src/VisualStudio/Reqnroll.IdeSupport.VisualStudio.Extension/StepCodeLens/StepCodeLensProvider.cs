#nullable enable
#pragma warning disable VSEXTPREVIEW_CODELENS

using System;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Extensibility;
using Microsoft.VisualStudio.Extensibility.Editor;
using Reqnroll.IdeSupport.Common.Diagnostics;
using Reqnroll.IdeSupport.VisualStudio.Extension.FindStepUsages;

namespace Reqnroll.IdeSupport.VisualStudio.Extension.StepCodeLens;

/// <summary>
/// VS.Extensibility CodeLens provider that shows a "N step usage(s)" adornment above each
/// step-binding attribute in a C# file (design doc F18).
/// </summary>
/// <remarks>
/// <para>
/// VS.Extensibility <c>ICodeLensProvider</c> is called once per C# code element (method,
/// property, type) in the active document.  Because VS's built-in C# tagger places code
/// elements at method level (not attribute level), this provider shows one lens per method.
/// The label aggregates usage counts for all step-binding attributes on that method.
/// </para>
/// <para>
/// Clicking a lens delegates to <see cref="FindStepUsagesService"/> + <see cref="FindStepUsagesRenderer"/>,
/// which reuse the F14 pipeline to open the Find All References window.
/// </para>
/// </remarks>
[VisualStudioContribution]
internal sealed class StepCodeLensProvider : ExtensionPart, ICodeLensProvider
{
    private readonly StepCodeLensState _state;
    private readonly TraceSource       _traceSource;
    private readonly IDeveroomLogger   _fileLogger = new SynchronousFileLogger();

    public StepCodeLensProvider(StepCodeLensState state, TraceSource traceSource)
    {
        _state       = state;
        _traceSource = traceSource;
    }

    // Apply to C# files only.
    public TextViewExtensionConfiguration TextViewExtensionConfiguration => new()
    {
        AppliesTo = [DocumentFilter.FromDocumentType("CSharp")]
    };

    // Provider display name shown in VS Tools > Options > Text Editor > Code Lens.
    public CodeLensProviderConfiguration CodeLensProviderConfiguration =>
        new("Reqnroll Step Usages");

    /// <inheritdoc />
    public Task<CodeLens?> TryCreateCodeLensAsync(
        CodeElement        codeElement,
        CodeElementContext context,
        CancellationToken  cancellationToken)
    {
        // Only create lenses for methods; types, properties etc. are ignored.
        if (codeElement.Kind != CodeElementKind.KnownValues.Method)
            return Task.FromResult<CodeLens?>(null);

        var fileUri   = context.Range.Document.Uri;
        // LineNumber is 0-based.
        var startLine = context.Range.Start.GetContainingLine().LineNumber;

        // Record this method's start line so GetLabelAsync can bound its attribute lookback.
        _state.RegisterMethodLine(fileUri.ToString(), startLine);
        _fileLogger.LogInfo($"StepCodeLensProvider.TryCreateCodeLensAsync: registered method at line {startLine} (0-based) in {fileUri}");

        var lens = new StepCodeLens(_state, _traceSource, fileUri, startLine);
        return Task.FromResult<CodeLens?>(lens);
    }
}

/// <summary>
/// A single step-usage code lens created for a C# method.  Aggregates usage counts for all
/// step-binding attributes that fall within a window just above the method declaration.
/// </summary>
internal sealed class StepCodeLens : InvokableCodeLens
{
    private readonly StepCodeLensState _state;
    private readonly TraceSource       _traceSource;
    private readonly IDeveroomLogger   _fileLogger = new SynchronousFileLogger();
    private readonly Uri               _fileUri;
    private readonly int               _methodStartLine;

    public StepCodeLens(
        StepCodeLensState state,
        TraceSource       traceSource,
        Uri               fileUri,
        int               methodStartLine)
    {
        _state           = state;
        _traceSource     = traceSource;
        _fileUri         = fileUri;
        _methodStartLine = methodStartLine;
        _state.RegisterLens(this, fileUri.ToString());
    }

    // VS.Extensibility reports the code-element range starting at the FIRST attribute line
    // (e.g. [Scope] or [Given]), not at the 'public void' declaration.  The server puts its
    // lens at the method-declaration line, which is always >= the first attribute line.
    // VS processes methods bottom-to-top, so by the time GetLabelAsync runs for method N,
    // the methods below it in the file (already processed) are already registered in the bag.
    // GetNextMethodLine therefore returns a reliable upper bound for the attribute block.
    // For the bottommost visible method (nothing below it registered yet) we fall back to a
    // fixed lookahead — generous enough to cover ~5 stacked attributes yet tight enough to
    // exclude the next method's lens even in compact code.
    private const int AttributeLookahead = 5;

    /// <summary>
    /// Returns the aggregated usage label for this method's step-binding attributes,
    /// or an empty label if the method is not a step binding.
    /// </summary>
    public override async Task<CodeLensLabel> GetLabelAsync(
        CodeElementContext context,
        CancellationToken  cancellationToken)
    {
        var service = _state.Service;
        if (service is null)
            return new CodeLensLabel { Text = string.Empty, Tooltip = string.Empty };

        try
        {
            var lenses = await service
                .GetLensesAsync(_fileUri.ToString(), cancellationToken)
                .ConfigureAwait(false);

            // context.Range.Start is the first-attribute line for C# methods.
            var currentStartLine = context.Range.Start.GetContainingLine().LineNumber;

            // Upper bound: the first-attribute line of the next method (already registered because
            // VS processes methods bottom-to-top).  Fall back to +AttributeLookahead when this is
            // the bottommost visible method and no next entry is in the bag yet.
            var nextMethod = _state.GetNextMethodLine(_fileUri.ToString(), currentStartLine);
            var upperBound = nextMethod >= 0 ? nextMethod : currentStartLine + AttributeLookahead;

            _fileLogger.LogInfo(
                $"StepCodeLens.GetLabelAsync: method at line {currentStartLine} (0-based), " +
                $"nextMethod={nextMethod}, upperBound={upperBound}, " +
                $"serverLensLines=[{string.Join(",", lenses.Select(l => l.RangeLine))}]");

            // Server lens lines are at the method-declaration line (>= currentStartLine).
            var attrLenses = lenses
                .Where(l => l.RangeLine >= currentStartLine && l.RangeLine < upperBound)
                .ToList();

            if (attrLenses.Count == 0)
                return new CodeLensLabel { Text = string.Empty, Tooltip = string.Empty };

            // Sum usage counts across all step-binding attributes on this method.
            var totalUsages = attrLenses
                .Select(l => ParseCount(l.Title))
                .Sum();

            var text    = totalUsages == 1 ? "1 step usage" : $"{totalUsages} step usages";
            var tooltip = "Reqnroll step usages for this binding";
            _fileLogger.LogInfo(
                $"StepCodeLens.GetLabelAsync: '{text}' for method at line {currentStartLine} in {_fileUri}");
            return new CodeLensLabel { Text = text, Tooltip = tooltip };
        }
        catch (Exception ex)
        {
            _traceSource.TraceEvent(TraceEventType.Warning, 0,
                "StepCodeLens.GetLabelAsync: failed for {0}:{1}: {2}", _fileUri, _methodStartLine, ex.Message);
            return new CodeLensLabel { Text = string.Empty, Tooltip = string.Empty };
        }
    }

    /// <summary>
    /// Opens the Find All References window for the first step-binding attribute found
    /// within the attribute-lookback window above this method.
    /// </summary>
    public override async Task ExecuteAsync(
        CodeElementContext context,
        IClientContext     clientContext,
        CancellationToken  cancellationToken)
    {
        var findService  = _state.FindUsagesService;
        var renderer     = _state.FindUsagesRenderer;
        if (findService is null || renderer is null)
        {
            _fileLogger.LogWarning(
                "StepCodeLens.ExecuteAsync: LSP server not yet initialized — cannot invoke find usages.");
            return;
        }

        try
        {
            var lensService = _state.Service;
            if (lensService is null) return;

            var lenses = await lensService
                .GetLensesAsync(_fileUri.ToString(), cancellationToken)
                .ConfigureAwait(false);

            var currentStartLine = context.Range.Start.GetContainingLine().LineNumber;
            var nextMethod2      = _state.GetNextMethodLine(_fileUri.ToString(), currentStartLine);
            var upperBound2      = nextMethod2 >= 0 ? nextMethod2 : currentStartLine + AttributeLookahead;

            // Use the first (topmost) server lens in this method's attribute block.
            var firstAttr = lenses
                .Where(l => l.RangeLine >= currentStartLine && l.RangeLine < upperBound2)
                .OrderBy(l => l.RangeLine)
                .FirstOrDefault();

            if (firstAttr is null) return;

            _fileLogger.LogInfo(
                $"StepCodeLens.ExecuteAsync: invoking find usages at {_fileUri}:{firstAttr.ArgLine}");

            var result = await findService
                .FindUsagesAsync(_fileUri.ToString(), firstAttr.ArgLine, 0, cancellationToken)
                .ConfigureAwait(false);

            if (!result.IsBinding) return;

            var count = result.Locations.Count;
            var label = count == 0
                ? "0 usages"
                : $"{count} usage{(count == 1 ? "" : "s")} of step definition";

            await renderer.RenderAsync(label, result, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _fileLogger.LogWarning($"StepCodeLens.ExecuteAsync: failed: {ex}");
            _traceSource.TraceEvent(TraceEventType.Error, 0,
                "StepCodeLens.ExecuteAsync: failed: {0}", ex);
        }
    }

    /// <inheritdoc />
    public override void Dispose()
    {
        _state.UnregisterLens(this, _fileUri.ToString());
    }

    /// <summary>
    /// Called by <see cref="StepCodeLensState.InvalidateLensesForFile"/> to trigger a
    /// fresh call to <see cref="GetLabelAsync"/> on VS's next paint cycle.
    /// </summary>
    internal void InvalidateLabel() => Invalidate();

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static int ParseCount(string title)
    {
        // Title formats: "1 step usage" or "N step usages" or "0 step usages"
        var space = title.IndexOf(' ');
        if (space > 0 && int.TryParse(title.Substring(0, space), out var n))
            return n;
        return 0;
    }
}
