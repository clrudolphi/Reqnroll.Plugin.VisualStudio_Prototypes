#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Reqnroll.IdeSupport.LSP.Server.Benchmarks.Corpus;
using Reqnroll.IdeSupport.LSP.Server.Benchmarks.Harness;
using Reqnroll.IdeSupport.LSP.Server.Benchmarks.Latency;
using Reqnroll.IdeSupport.LSP.Server.Benchmarks.Reporting;
using Reqnroll.IdeSupport.LSP.Server.Benchmarks.Scenarios;

namespace Reqnroll.IdeSupport.LSP.Server.Benchmarks;

/// <summary>
/// Drives the §9 Layer 2 interactive benchmark suite against the committed corpus and reports
/// per-operation latency percentiles. Absolute §9 thresholds are asserted only on a designated
/// reference machine (or with <c>--assert</c>); elsewhere the run is report-only and exits 0.
/// </summary>
public static class BenchmarkRunner
{
    public static async Task<int> RunAsync(string[] args)
    {
        var warmup = IntArg(args, "--warmup", 10);
        var measured = IntArg(args, "--iterations", 50);
        var fileCount = IntArg(args, "--files", 10);
        var outPath = StringArg(args, "--out");
        var corpusAssembly = StringArg(args, "--corpus-assembly");
        var includeBatch = !args.Contains("--no-batch");
        var gate = ReferenceMachine.FromEnvironment(args);

        var corpusRoot = CorpusLocator.FindCorpusRoot();
        var manifest = CorpusManifest.Load(CorpusLocator.ManifestPath(corpusRoot));

        Console.WriteLine($"Running interactive benchmarks against corpus at {corpusRoot}");
        Console.WriteLine($"  warmup={warmup} iterations={measured} files={fileCount} " +
                          $"assert={gate.AssertThresholds}");

        await using var harness = new BenchmarkLspHarness();
        await harness.StartAsync(corpusRoot).ConfigureAwait(false);

        var features = await InteractiveScenarios.OpenFeaturesAsync(harness, corpusRoot, fileCount).ConfigureAwait(false);
        var scenarios = new InteractiveScenarios(harness, features, warmup, measured);

        var summaries = new List<(PerfTarget Target, LatencySummary Summary)>
        {
            (PerfTargets.SemanticTokensFull, await scenarios.SemanticTokensAsync().ConfigureAwait(false)),
            (PerfTargets.CompletionKeyword, await scenarios.KeywordCompletionAsync().ConfigureAwait(false)),
            (PerfTargets.CompletionStep, await scenarios.StepCompletionAsync().ConfigureAwait(false)),
            (PerfTargets.DefinitionCacheHit, await scenarios.DefinitionAsync().ConfigureAwait(false)),
            (PerfTargets.PublishDiagnostics, await scenarios.DiagnosticsPushAsync().ConfigureAwait(false)),
        };

        // Batch scenarios (coarse wall-clock). Cold start spins up fresh servers, so it is opt-out
        // via --no-batch for quick interactive-only runs.
        if (includeBatch)
        {
            Console.WriteLine("Running batch scenarios (cold-start scan)...");
            summaries.Add((PerfTargets.ColdStartScan,
                await BatchScenarios.ColdStartScanAsync(corpusRoot).ConfigureAwait(false)));
        }

        var skipped = BatchScenarios.UnavailableDiscoveryScenarios(corpusAssembly);

        var results = summaries.Select(s => new OperationResult(s.Target, s.Summary)).ToList();
        var report = new BenchmarkReport(
            MachineName: Environment.MachineName,
            AssertThresholds: gate.AssertThresholds,
            TimestampUtc: DateTimeOffset.UtcNow,
            CorpusDescription: $"{manifest.Fingerprint.FeatureFileCount} features, " +
                               $"{manifest.Fingerprint.StepDefinitionPatternCount} patterns, " +
                               $"{manifest.Fingerprint.StepCount} steps",
            Results: results,
            Skipped: skipped);

        Console.WriteLine();
        Console.WriteLine(report.ToConsoleTable());

        if (!string.IsNullOrEmpty(outPath))
        {
            File.WriteAllText(outPath, report.ToJson());
            Console.WriteLine($"Wrote results JSON to {outPath}");
        }

        // §9 gating: fail the process only on a designated reference machine.
        if (gate.AssertThresholds && !report.AllPassed)
        {
            Console.Error.WriteLine("FAIL: one or more §9 targets missed on the reference machine.");
            return 1;
        }

        return 0;
    }

    private static int IntArg(string[] args, string name, int fallback)
    {
        var idx = Array.IndexOf(args, name);
        return idx >= 0 && idx + 1 < args.Length && int.TryParse(args[idx + 1], out var v) ? v : fallback;
    }

    private static string? StringArg(string[] args, string name)
    {
        var idx = Array.IndexOf(args, name);
        return idx >= 0 && idx + 1 < args.Length ? args[idx + 1] : null;
    }
}
