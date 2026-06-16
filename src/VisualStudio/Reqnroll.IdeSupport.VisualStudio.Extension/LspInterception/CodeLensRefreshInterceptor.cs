#nullable enable

using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using Reqnroll.IdeSupport.VisualStudio.Extension.StepCodeLens;

namespace Reqnroll.IdeSupport.VisualStudio.Extension.LspInterception;

/// <summary>
/// Keeps the VS C# step code lenses in sync with the server's binding registry. Two triggers:
/// <list type="bullet">
///   <item>(send) <c>textDocument/didChange</c> on a <c>.cs</c> file — after a debounced delay
///   (to let the server process the edit), invalidates the tracked lenses for that file; and</item>
///   <item>(receive) <c>reqnroll/refreshCodeLens</c> pushed by the server after a full binding-registry
///   replacement (e.g. startup connector discovery) — invalidates <em>all</em> tracked lenses, so a
///   <c>.cs</c> file that was the foreground editor before the server was ready picks up its usage
///   counts without the user having to switch tabs.</item>
/// </list>
/// Invalidation re-calls <see cref="StepCodeLens.GetLabelAsync"/> with fresh data.
/// </summary>
internal sealed class CodeLensRefreshInterceptor : ILspMessageInterceptor
{
    private readonly StepCodeLensState _state;
    private readonly TraceSource       _traceSource;

    // Debounce: don't invalidate more than once per 500ms for the same file.
    private static readonly TimeSpan DebounceInterval = TimeSpan.FromMilliseconds(500);
    private string? _lastFileUri;
    private DateTime _lastInvalidation = DateTime.MinValue;

    public CodeLensRefreshInterceptor(StepCodeLensState state, TraceSource traceSource)
    {
        _state       = state;
        _traceSource = traceSource;
    }

    public Task<LspInterceptorResult> InterceptAsync(
        LspMessage message,
        CancellationToken cancellationToken)
    {
        var body = message.Body;
        if (body is null)
            return Task.FromResult(LspInterceptorResult.PassThrough);

        var method = body["method"]?.Value<string>();
        if (method is null)
            return Task.FromResult(LspInterceptorResult.PassThrough);

        // Server→client: full binding-registry replacement completed. Re-pull every tracked lens,
        // because lenses for an already-open .cs file were rendered before the server had counts.
        if (message.Direction == LspMessageDirection.Receive)
        {
            if (string.Equals(method, "reqnroll/refreshCodeLens", StringComparison.Ordinal))
            {
                InvalidateOnUiThread(uri: null);
                _traceSource.TraceInformation(
                    "CodeLensRefreshInterceptor: refreshed all tracked lenses on server signal.");
            }
            return Task.FromResult(LspInterceptorResult.PassThrough);
        }

        if (message.Direction != LspMessageDirection.Send)
            return Task.FromResult(LspInterceptorResult.PassThrough);

        if (!string.Equals(method, "textDocument/didChange", StringComparison.Ordinal))
            return Task.FromResult(LspInterceptorResult.PassThrough);

        // Extract the URI from the params
        var uri = body["params"]?["textDocument"]?["uri"]?.Value<string>();
        if (string.IsNullOrEmpty(uri))
            return Task.FromResult(LspInterceptorResult.PassThrough);

        // Only care about .cs files
        if (!uri!.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
            return Task.FromResult(LspInterceptorResult.PassThrough);

        // Debounce: skip if we just invalidated this file
        var now = DateTime.UtcNow;
        if (string.Equals(uri, _lastFileUri, StringComparison.OrdinalIgnoreCase) &&
            (now - _lastInvalidation) < DebounceInterval)
            return Task.FromResult(LspInterceptorResult.PassThrough);

        _lastFileUri       = uri;
        _lastInvalidation  = now;

        InvalidateOnUiThread(uri);

        _traceSource.TraceInformation(
            "CodeLensRefreshInterceptor: invalidated lenses for '{0}'", uri);

        return Task.FromResult(LspInterceptorResult.PassThrough);
    }

    /// <summary>
    /// Invalidates tracked lenses on the UI thread. Passing a <paramref name="uri"/> refreshes that
    /// single file; passing <see langword="null"/> refreshes every tracked file.
    /// </summary>
    /// <remarks>
    /// Must run on the UI thread — <c>CodeLens.Invalidate()</c> in the VS Extensibility SDK sets an
    /// internal dirty flag that only takes effect when called from the main thread.
    /// </remarks>
    private void InvalidateOnUiThread(string? uri)
    {
        var jtf = Microsoft.VisualStudio.Shell.ThreadHelper.JoinableTaskFactory;
        _ = jtf.RunAsync(async () =>
        {
            await jtf.SwitchToMainThreadAsync();
            if (uri is null)
                _state.InvalidateAllTrackedLenses();
            else
                _state.InvalidateLensesForFile(uri);
        });
    }
}
