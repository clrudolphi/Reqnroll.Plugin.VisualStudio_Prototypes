#nullable enable

using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using Reqnroll.IdeSupport.Common.Diagnostics;
using Reqnroll.IdeSupport.VisualStudio.Extension.LspInterception;

namespace Reqnroll.IdeSupport.VisualStudio.Extension.FindStepUsages;

/// <summary>
/// Shared core for all three "Find Step Usages" surfaces (design doc F14 P3).
/// Sends a custom <c>reqnroll/findStepUsages</c> request over the owned
/// <see cref="LspInterceptingPipe"/> and maps the result to a <see cref="StepUsagesResult"/>.
/// </summary>
/// <remarks>
/// Uses the custom <c>reqnroll/findStepUsages</c> request (F14 P2b) rather than
/// <c>textDocument/references</c> to obtain the full three-state contract:
/// <list type="bullet">
///   <item>Server returns JSON <c>null</c> → <see cref="StepUsagesResult.NotABinding"/> (Surface 3 falls through).</item>
///   <item>Server returns <c>{"isBinding":true,"locations":[]}</c> → binding present, 0 usages.</item>
///   <item>Server returns <c>{"isBinding":true,"locations":[...]}</c> → matching feature-file steps.</item>
/// </list>
/// Each location includes a <c>stepText</c> field supplied directly by the server from the
/// in-memory document snapshot, so no disk I/O is required on the client side.
/// </remarks>
internal sealed class FindStepUsagesService
{
    // Method name for the custom request — distinct from textDocument/references so the server
    // can deliver null and per-location stepText that the standard LSP method cannot carry.
    private const string RequestMethod = "reqnroll/findStepUsages";

    private readonly LspInterceptingPipe _pipe;
    private readonly TraceSource         _traceSource;
    // TraceSource is not routed to the shared reqnroll-vs-debug-*.log, and the injected
    // request/response bypasses the inspector log (the response is consumed before the
    // interceptors run).  Mirror the raw server result here so the one signal that pins down
    // client-side vs server-side failure is visible in a single diagnostic run.
    private readonly IDeveroomLogger     _fileLogger = new SynchronousFileLogger();

    public FindStepUsagesService(LspInterceptingPipe pipe, TraceSource traceSource)
    {
        _pipe        = pipe;
        _traceSource = traceSource;
    }

    /// <summary>
    /// Queries the LSP server for step usages at <paramref name="line0"/> / <paramref name="char0"/>
    /// in <paramref name="fileUri"/> (all 0-based).
    /// </summary>
    public async Task<StepUsagesResult> FindUsagesAsync(
        string            fileUri,
        int               line0,
        int               char0,
        CancellationToken cancellationToken)
    {
        var paramsJson = BuildParams(fileUri, line0, char0);

        _traceSource.TraceInformation(
            "FindStepUsagesService: querying {0} at {1}:{2}:{3}", RequestMethod, fileUri, line0, char0);

        _fileLogger.LogInfo(
            $"FindStepUsagesService: sending {RequestMethod} params={paramsJson}");

        var result = await _pipe
            .SendRequestToServerAsync(RequestMethod, paramsJson, cancellationToken)
            .ConfigureAwait(false);

        // NOTE: use the parameterless JToken.ToString() — the overload that takes
        // Newtonsoft.Json.Formatting throws MissingMethodException against the Newtonsoft version
        // that VS loads at runtime.
        _fileLogger.LogInfo(
            $"FindStepUsagesService: raw server result = {(result is null ? "<null>" : result.ToString())}");

        // The server returns {isBinding:false} when the caret is not on a binding.
        // (Returning JSON null is avoided: OmniSharp's OnRequest framework sends an error response
        //  rather than serialising null for custom response types.)
        // Guard for null here anyway in case the server version is mismatched.
        if (result is null || result.Type == JTokenType.Null)
        {
            _traceSource.TraceInformation(
                "FindStepUsagesService: server returned null (unexpected) — treating as NotABinding");
            return StepUsagesResult.NotABinding;
        }

        if (result is JObject obj)
        {
            var isBinding = obj["isBinding"]?.Value<bool>() ?? false;
            if (!isBinding)
            {
                _traceSource.TraceInformation(
                    "FindStepUsagesService: response has isBinding=false — NotABinding (fall through)");
                return StepUsagesResult.NotABinding;
            }

            var locationsArray = obj["locations"] as JArray ?? new JArray();
            var locations = ParseLocations(locationsArray);
            _traceSource.TraceInformation(
                "FindStepUsagesService: {0} location(s) returned", locations.Count);
            return new StepUsagesResult(locations);
        }

        _traceSource.TraceInformation(
            "FindStepUsagesService: unexpected result token type {0} — NotABinding",
            result.Type);
        return StepUsagesResult.NotABinding;
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static string BuildParams(string fileUri, int line0, int char0)
    {
        // Same request params shape as textDocument/references (textDocument URI + position).
        // includeDeclaration omitted — reqnroll/findStepUsages ignores it but keep the field
        // for structural parity so any future tracing is recognisable as a references variant.
        var escapedUri = Newtonsoft.Json.JsonConvert.ToString(fileUri);
        return $"{{\"textDocument\":{{\"uri\":{escapedUri}}},\"position\":{{\"line\":{line0},\"character\":{char0}}},\"context\":{{\"includeDeclaration\":false}}}}";
    }

    private static IReadOnlyList<StepUsageLocation> ParseLocations(JArray array)
    {
        var result = new List<StepUsageLocation>(array.Count);
        foreach (var item in array)
        {
            if (item is not JObject obj) continue;

            var uri = obj["uri"]?.Value<string>();
            if (uri is null) continue;

            var stepText    = obj["stepText"]?.Value<string>();
            var keyword     = obj["keyword"]?.Value<string>();
            var scenarioName = obj["scenarioName"]?.Value<string>();
            var projectName  = obj["projectName"]?.Value<string>();

            var startLine = obj["startLine"]?.Value<int>() ?? 0;
            var startChar = obj["startChar"]?.Value<int>() ?? 0;
            var endLine   = obj["endLine"]?.Value<int>()   ?? 0;
            var endChar   = obj["endChar"]?.Value<int>()   ?? 0;

            result.Add(new StepUsageLocation(
                uri, startLine, startChar, endLine, endChar,
                stepText, keyword, scenarioName, projectName));
        }
        return result;
    }
}
