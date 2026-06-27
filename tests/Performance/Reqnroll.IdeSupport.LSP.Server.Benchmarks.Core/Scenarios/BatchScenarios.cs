#nullable enable

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using OmniSharp.Extensions.LanguageServer.Protocol;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using Reqnroll.IdeSupport.LSP.Server.Benchmarks.Harness;
using Reqnroll.IdeSupport.LSP.Server.Benchmarks.Latency;

namespace Reqnroll.IdeSupport.LSP.Server.Benchmarks.Scenarios;

/// <summary>
/// The §9 batch / throughput scenarios, confirmed with wall-clock timing (these targets are coarse
/// enough not to need protocol-boundary percentiles). Cold start is measured by spinning up fresh
/// in-process servers; the binding-discovery scenarios are measured only when a built corpus
/// assembly is supplied (otherwise they are reported as skipped, never faked).
/// </summary>
public static class BatchScenarios
{
    /// <summary>
    /// Cold-start scan: for each repetition, start a fresh server, complete the initialize
    /// handshake, open every corpus feature file, and wait until the first file yields semantic
    /// tokens — i.e. the workspace is parsed and serviceable. Reports the wall-clock distribution.
    /// </summary>
    public static async Task<LatencySummary> ColdStartScanAsync(string corpusRoot, int repetitions = 3)
    {
        var recorder = new LatencyRecorder(PerfTargets.ColdStartScan.Operation);
        var featurePaths = Directory
            .EnumerateFiles(Path.Combine(corpusRoot, "Features"), "*.feature", SearchOption.AllDirectories)
            .OrderBy(p => p, StringComparer.Ordinal)
            .ToArray();

        for (var rep = 0; rep < repetitions; rep++)
        {
            var start = Stopwatch.GetTimestamp();

            await using var harness = new BenchmarkLspHarness();
            await harness.StartAsync(corpusRoot).ConfigureAwait(false);

            DocumentUri? firstUri = null;
            foreach (var path in featurePaths)
            {
                var uri = DocumentUri.FromFileSystemPath(path);
                firstUri ??= uri;
                harness.OpenFeature(uri, 1, File.ReadAllText(path));
            }

            // Wait until the workspace is serviceable (first file parsed).
            var deadline = DateTime.UtcNow.AddSeconds(30);
            while (DateTime.UtcNow < deadline)
            {
                var tokens = await harness.RequestAsync<SemanticTokens?>(
                    "textDocument/semanticTokens/full",
                    new SemanticTokensParams { TextDocument = new TextDocumentIdentifier { Uri = firstUri! } })
                    .ConfigureAwait(false);
                if (tokens is { Data.Length: > 0 }) break;
                await Task.Delay(25).ConfigureAwait(false);
            }

            recorder.Add(Stopwatch.GetElapsedTime(start).TotalMilliseconds);
        }

        return recorder.Summarize();
    }

    /// <summary>
    /// The binding-discovery batch scenarios (Roslyn single-file re-discovery; reflection post-build
    /// discovery) require a <b>built</b> corpus assembly + the connector, which the committed
    /// source-only corpus does not include. Until that is wired (see the implementation plan), these
    /// are reported as skipped rather than measured against an empty registry.
    /// </summary>
    public static IReadOnlyList<SkippedBatchScenario> UnavailableDiscoveryScenarios(string? corpusAssemblyPath)
    {
        if (!string.IsNullOrEmpty(corpusAssemblyPath) && File.Exists(corpusAssemblyPath))
            return Array.Empty<SkippedBatchScenario>();

        const string reason = "requires a built corpus bindings assembly (not part of the source-only corpus)";
        return new[]
        {
            new SkippedBatchScenario(PerfTargets.RoslynReDiscovery, reason),
            new SkippedBatchScenario(PerfTargets.ReflectionDiscovery, reason),
        };
    }
}

/// <summary>A batch scenario that could not be measured, with the reason, for honest reporting.</summary>
public sealed record SkippedBatchScenario(PerfTarget Target, string Reason);
