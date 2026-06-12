#nullable enable

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using Reqnroll.IdeSupport.Common.Diagnostics;
using Reqnroll.IdeSupport.VisualStudio.Extension.LspInterception;

namespace Reqnroll.IdeSupport.VisualStudio.Extension.FindUnusedStepDefinitions;

/// <summary>
/// Sends a custom <c>reqnroll/findUnusedStepDefinitions</c> request over the owned
/// <see cref="LspInterceptingPipe"/> and maps the result to an
/// <see cref="UnusedStepDefinitionsResult"/>.
/// </summary>
internal sealed class FindUnusedStepDefinitionsService
{
    private const string RequestMethod = "reqnroll/findUnusedStepDefinitions";

    private readonly LspInterceptingPipe _pipe;
    private readonly TraceSource         _traceSource;
    private readonly IDeveroomLogger     _fileLogger = new SynchronousFileLogger();

    public FindUnusedStepDefinitionsService(LspInterceptingPipe pipe, TraceSource traceSource)
    {
        _pipe        = pipe;
        _traceSource = traceSource;
    }

    public async Task<UnusedStepDefinitionsResult> FindUnusedAsync(CancellationToken cancellationToken)
    {
        _traceSource.TraceInformation("FindUnusedStepDefinitionsService: sending {0}", RequestMethod);
        _fileLogger.LogInfo($"FindUnusedStepDefinitionsService: sending {RequestMethod}");

        // Empty params object — the server ignores the body.
        const string emptyParams = "{}";

        var result = await _pipe
            .SendRequestToServerAsync(RequestMethod, emptyParams, cancellationToken)
            .ConfigureAwait(false);

        _fileLogger.LogInfo(
            $"FindUnusedStepDefinitionsService: raw result = " +
            $"{(result is null ? "<null>" : result.ToString())}");

        if (result is null || result.Type == JTokenType.Null)
        {
            _traceSource.TraceInformation(
                "FindUnusedStepDefinitionsService: server returned null — empty result");
            return UnusedStepDefinitionsResult.Empty;
        }

        if (result is JObject obj)
        {
            var items = ParseItems(obj["items"] as JArray ?? new JArray());
            _traceSource.TraceInformation(
                "FindUnusedStepDefinitionsService: {0} unused step definition(s)", items.Count);
            return new UnusedStepDefinitionsResult(items);
        }

        _traceSource.TraceInformation(
            "FindUnusedStepDefinitionsService: unexpected token type {0}", result.Type);
        return UnusedStepDefinitionsResult.Empty;
    }

    private static IReadOnlyList<UnusedStepLocation> ParseItems(JArray array)
    {
        var result = new List<UnusedStepLocation>(array.Count);
        foreach (var token in array)
        {
            if (token is not JObject item) continue;
            result.Add(new UnusedStepLocation
            {
                ProjectName       = item["projectName"]?.Value<string>(),
                ClassName         = item["className"]?.Value<string>(),
                MethodName        = item["methodName"]?.Value<string>(),
                BindingExpression = item["bindingExpression"]?.Value<string>(),
                SourceFile        = item["sourceFile"]?.Value<string>(),
                SourceLine        = item["sourceLine"]?.Value<int>() ?? 0,
                SourceChar        = item["sourceChar"]?.Value<int>() ?? 0,
            });
        }
        return result;
    }
}
