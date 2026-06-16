#nullable enable

using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using Reqnroll.IdeSupport.VisualStudio.Extension.FindStepUsages;

namespace Reqnroll.IdeSupport.VisualStudio.Extension.StepCodeLens;

/// <summary>
/// Container-registered singleton holder for the runtime-created F18 "Step Code Lens" components.
/// </summary>
/// <remarks>
/// <see cref="StepCodeLensService"/> depends on <c>LspInterceptingPipe</c>, which only exists
/// after the language server connection is established — too late for plain DI construction.
/// <see cref="ReqnrollLanguageClient"/> populates this on server init and clears it on dispose;
/// <see cref="StepCodeLensProvider"/> reads it via constructor injection.
/// The <see cref="FindUsagesService"/> and <see cref="FindUsagesRenderer"/> are borrowed from the
/// F14 state because the execute-lens action reuses the same FAR-window rendering pipeline.
/// </remarks>
internal sealed class StepCodeLensState
{
    /// <summary>Set once the server has initialised; null before that and after dispose.</summary>
    public StepCodeLensService?     Service          { get; set; }

    /// <summary>F14 service reused for the execute-lens find-step-usages action.</summary>
    public FindStepUsagesService?   FindUsagesService  { get; set; }

    /// <summary>F14 renderer reused for the execute-lens FAR window display.</summary>
    public FindStepUsagesRenderer?  FindUsagesRenderer { get; set; }

    // Per-file registry of method start lines (0-based).  Used by GetLabelAsync to bound the
    // attribute-lookback to the current method's own attribute block, not the previous method's.
    private readonly ConcurrentDictionary<string, ConcurrentBag<int>> _methodStartLines
        = new(System.StringComparer.OrdinalIgnoreCase);

    internal void RegisterMethodLine(string fileUri, int startLine)
        => _methodStartLines.GetOrAdd(fileUri, _ => new ConcurrentBag<int>()).Add(startLine);

    /// <summary>
    /// Returns the smallest registered method start line that is strictly greater than
    /// <paramref name="currentStartLine"/>, or -1 when no later method is on record.
    /// VS processes methods bottom-to-top, so by the time GetLabelAsync runs for method N,
    /// the methods below it (higher line numbers) are already registered — making this
    /// a reliable upper-bound source.
    /// </summary>
    internal int GetNextMethodLine(string fileUri, int currentStartLine)
    {
        if (!_methodStartLines.TryGetValue(fileUri, out var bag))
            return -1;
        return bag.Where(l => l > currentStartLine).DefaultIfEmpty(-1).Min();
    }

    // ── Lens invalidation ────────────────────────────────────────────────────
    //
    // Track StepCodeLens instances so they can be invalidated when the binding
    // registry changes (triggering VS to re-call GetLabelAsync).  Without this,
    // the codeLens count would only refresh when the user navigates away and
    // back to the .cs file.

    private readonly ConcurrentDictionary<string, List<WeakReference<StepCodeLens>>> _lensesByFile
        = new(System.StringComparer.OrdinalIgnoreCase);
    private readonly object _lensesLock = new();

    internal void RegisterLens(StepCodeLens lens, string fileUri)
    {
        lock (_lensesLock)
        {
            var list = _lensesByFile.GetOrAdd(fileUri, _ => new List<WeakReference<StepCodeLens>>());
            list.Add(new WeakReference<StepCodeLens>(lens));
        }
    }

    internal void UnregisterLens(StepCodeLens lens, string fileUri)
    {
        lock (_lensesLock)
        {
            if (_lensesByFile.TryGetValue(fileUri, out var list))
            {
                list.RemoveAll(w => !w.TryGetTarget(out var t) || t == lens);
                if (list.Count == 0)
                    _lensesByFile.TryRemove(fileUri, out _);
            }
        }
    }

    /// <summary>
    /// Invalidates all tracked code lenses for <paramref name="fileUri"/>, causing
    /// VS to re-call <see cref="StepCodeLens.GetLabelAsync"/> on the next paint cycle.
    /// Safe to call from any thread.
    /// </summary>
    internal void InvalidateLensesForFile(string fileUri)
    {
        lock (_lensesLock)
        {
            if (!_lensesByFile.TryGetValue(fileUri, out var list))
                return;

            // Snapshot and sweep dead references
            var alive = new List<WeakReference<StepCodeLens>>(list.Count);
            foreach (var w in list)
            {
                if (w.TryGetTarget(out var lens))
                {
                    lens.InvalidateLabel();
                    alive.Add(w);
                }
            }
            _lensesByFile[fileUri] = alive;
        }
    }

    /// <summary>
    /// Invalidates every tracked code lens across all files, causing VS to re-call
    /// <see cref="StepCodeLens.GetLabelAsync"/> for each. Used when the server signals a full
    /// binding-registry replacement (e.g. startup connector discovery): the lenses for any open
    /// <c>.cs</c> file were rendered before the server had usage counts, so they all need a re-pull.
    /// Safe to call from any thread.
    /// </summary>
    internal void InvalidateAllTrackedLenses()
    {
        List<string> fileUris;
        lock (_lensesLock)
        {
            fileUris = _lensesByFile.Keys.ToList();
        }

        foreach (var fileUri in fileUris)
            InvalidateLensesForFile(fileUri);
    }
}
