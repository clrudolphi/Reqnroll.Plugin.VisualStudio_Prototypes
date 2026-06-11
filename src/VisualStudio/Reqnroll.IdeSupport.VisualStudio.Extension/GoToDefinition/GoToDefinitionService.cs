#nullable enable

using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using Reqnroll.IdeSupport.Common.Diagnostics;
using Reqnroll.IdeSupport.VisualStudio.Extension.LspInterception;

namespace Reqnroll.IdeSupport.VisualStudio.Extension.GoToDefinition;

/// <summary>
/// Sends a <c>reqnroll/goToStepDefinitions</c> request to the LSP server and maps the response
/// to a <see cref="GoToDefinitionResult"/> (design doc F5).
/// </summary>
/// <remarks>
/// Uses the custom <c>reqnroll/goToStepDefinitions</c> message rather than the standard
/// <c>textDocument/definition</c> because the standard response carries only a file URI and
/// position — not the step-keyword type or C# method name needed to label the picker entries.
/// The standard handler is retained for generic LSP clients.
/// </remarks>
internal sealed class GoToDefinitionService
{
    private const string RequestMethod = "reqnroll/goToStepDefinitions";

    private readonly LspInterceptingPipe _pipe;
    private readonly TraceSource         _traceSource;
    private readonly IDeveroomLogger     _fileLogger = new SynchronousFileLogger();

    public GoToDefinitionService(LspInterceptingPipe pipe, TraceSource traceSource)
    {
        _pipe        = pipe;
        _traceSource = traceSource;
    }

    /// <summary>
    /// Queries the LSP server for step-definition bindings at <paramref name="line0"/> /
    /// <paramref name="char0"/> in <paramref name="fileUri"/> (all 0-based).
    /// </summary>
    public async Task<GoToDefinitionResult> GoToDefinitionAsync(
        string            fileUri,
        int               line0,
        int               char0,
        CancellationToken cancellationToken)
    {
        var paramsJson = BuildParams(fileUri, line0, char0);

        _traceSource.TraceInformation(
            "GoToDefinitionService: querying {0} at {1}:{2}:{3}", RequestMethod, fileUri, line0, char0);
        _fileLogger.LogInfo(
            $"GoToDefinitionService: sending {RequestMethod} params={paramsJson}");

        var result = await _pipe
            .SendRequestToServerAsync(RequestMethod, paramsJson, cancellationToken)
            .ConfigureAwait(false);

        _fileLogger.LogInfo(
            $"GoToDefinitionService: raw server result = {(result is null ? "<null>" : result.ToString())}");

        if (result is null || result.Type == JTokenType.Null)
        {
            _traceSource.TraceInformation("GoToDefinitionService: server returned null — no locations");
            return GoToDefinitionResult.Empty;
        }

        if (result is JObject obj)
        {
            var array     = obj["stepDefinitions"] as JArray ?? new JArray();
            var locations = ParseLocations(array);
            _traceSource.TraceInformation("GoToDefinitionService: {0} location(s) returned", locations.Count);
            return new GoToDefinitionResult(locations);
        }

        _traceSource.TraceInformation(
            "GoToDefinitionService: unexpected result token type {0}", result.Type);
        return GoToDefinitionResult.Empty;
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static string BuildParams(string fileUri, int line0, int char0)
    {
        var escapedUri = Newtonsoft.Json.JsonConvert.ToString(fileUri);
        return $"{{\"textDocument\":{{\"uri\":{escapedUri}}},\"position\":{{\"line\":{line0},\"character\":{char0}}}}}";
    }

    private static IReadOnlyList<StepDefinitionLocation> ParseLocations(JArray array)
    {
        var result = new List<StepDefinitionLocation>(array.Count);
        foreach (var item in array)
        {
            if (item is not JObject obj) continue;

            var uri = obj["uri"]?.Value<string>();
            if (uri is null) continue;

            var startLine  = obj["startLine"]?.Value<int>()  ?? 0;
            var startChar  = obj["startChar"]?.Value<int>()  ?? 0;
            var stepType   = obj["stepType"]?.Value<string>()   ?? "";
            var methodName = obj["methodName"]?.Value<string>() ?? "";

            result.Add(new StepDefinitionLocation(uri, startLine, startChar, stepType, methodName));
        }
        return result;
    }
}
