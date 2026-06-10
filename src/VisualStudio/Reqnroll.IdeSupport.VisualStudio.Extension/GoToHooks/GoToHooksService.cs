#nullable enable

using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using Reqnroll.IdeSupport.Common.Diagnostics;
using Reqnroll.IdeSupport.VisualStudio.Extension.LspInterception;

namespace Reqnroll.IdeSupport.VisualStudio.Extension.GoToHooks;

/// <summary>
/// Sends a custom <c>reqnroll/goToHooks</c> request to the LSP server and maps the result
/// to a <see cref="GoToHooksResult"/> (design doc F17).
/// </summary>
/// <remarks>
/// Uses the custom <c>reqnroll/goToHooks</c> message rather than <c>textDocument/definition</c>
/// because F5 (Go to Step Definition) already uses that message on step lines; the server cannot
/// distinguish "find step binding" from "find hooks" from position alone, and step-level hooks
/// (<c>[BeforeStep]</c> / <c>[AfterStep]</c>) would be unreachable via the shared message.
/// </remarks>
internal sealed class GoToHooksService
{
    private const string RequestMethod = "reqnroll/goToHooks";

    private readonly LspInterceptingPipe _pipe;
    private readonly TraceSource         _traceSource;
    private readonly IDeveroomLogger     _fileLogger = new SynchronousFileLogger();

    public GoToHooksService(LspInterceptingPipe pipe, TraceSource traceSource)
    {
        _pipe        = pipe;
        _traceSource = traceSource;
    }

    /// <summary>
    /// Queries the LSP server for applicable hooks at <paramref name="line0"/> /
    /// <paramref name="char0"/> in <paramref name="fileUri"/> (all 0-based).
    /// </summary>
    public async Task<GoToHooksResult> GoToHooksAsync(
        string            fileUri,
        int               line0,
        int               char0,
        CancellationToken cancellationToken)
    {
        var paramsJson = BuildParams(fileUri, line0, char0);

        _traceSource.TraceInformation(
            "GoToHooksService: querying {0} at {1}:{2}:{3}", RequestMethod, fileUri, line0, char0);
        _fileLogger.LogInfo(
            $"GoToHooksService: sending {RequestMethod} params={paramsJson}");

        var result = await _pipe
            .SendRequestToServerAsync(RequestMethod, paramsJson, cancellationToken)
            .ConfigureAwait(false);

        _fileLogger.LogInfo(
            $"GoToHooksService: raw server result = {(result is null ? "<null>" : result.ToString())}");

        if (result is null || result.Type == JTokenType.Null)
        {
            _traceSource.TraceInformation("GoToHooksService: server returned null — no hooks");
            return GoToHooksResult.Empty;
        }

        if (result is JObject obj)
        {
            var hooksArray = obj["hooks"] as JArray ?? new JArray();
            var hooks      = ParseHooks(hooksArray);
            _traceSource.TraceInformation("GoToHooksService: {0} hook(s) returned", hooks.Count);
            return new GoToHooksResult(hooks);
        }

        _traceSource.TraceInformation(
            "GoToHooksService: unexpected result token type {0}", result.Type);
        return GoToHooksResult.Empty;
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static string BuildParams(string fileUri, int line0, int char0)
    {
        var escapedUri = Newtonsoft.Json.JsonConvert.ToString(fileUri);
        return $"{{\"textDocument\":{{\"uri\":{escapedUri}}},\"position\":{{\"line\":{line0},\"character\":{char0}}}}}";
    }

    private static IReadOnlyList<HookLocation> ParseHooks(JArray array)
    {
        var result = new List<HookLocation>(array.Count);
        foreach (var item in array)
        {
            if (item is not JObject obj) continue;

            var uri        = obj["uri"]?.Value<string>();
            if (uri is null) continue;

            var startLine  = obj["startLine"]?.Value<int>()  ?? 0;
            var startChar  = obj["startChar"]?.Value<int>()  ?? 0;
            var hookType   = obj["hookType"]?.Value<string>() ?? "";
            var hookOrder  = obj["hookOrder"]?.Value<int>()  ?? 10000;
            var methodName = obj["methodName"]?.Value<string>() ?? "";

            result.Add(new HookLocation(uri, startLine, startChar, hookType, hookOrder, methodName));
        }
        return result;
    }
}
