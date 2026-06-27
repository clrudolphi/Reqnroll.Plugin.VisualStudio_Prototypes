#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Reqnroll.IdeSupport.LSP.Server.Benchmarks.Latency;
using Reqnroll.IdeSupport.LSP.Server.Benchmarks.Scenarios;

namespace Reqnroll.IdeSupport.LSP.Server.Benchmarks.Reporting;

/// <summary>
/// The full result of a benchmark run: the per-operation results plus the run context (machine,
/// timestamp, corpus fingerprint hash). Rendered to a console table and to JSON — the JSON is also
/// the Layer 3 baseline format (compare a future run's JSON against a stored baseline).
/// </summary>
public sealed record BenchmarkReport(
    string MachineName,
    bool AssertThresholds,
    DateTimeOffset TimestampUtc,
    string CorpusDescription,
    IReadOnlyList<OperationResult> Results,
    IReadOnlyList<SkippedBatchScenario>? Skipped = null)
{
    /// <summary>True when every asserted operation met its §9 target.</summary>
    public bool AllPassed => Results.All(r => r.MeetsTarget);

    public string ToConsoleTable() => ConsoleReporter.Render(this);

    public string ToJson() => JsonReporter.Render(this);
}

/// <summary>Renders a <see cref="BenchmarkReport"/> as a fixed-width console table.</summary>
public static class ConsoleReporter
{
    public static string Render(BenchmarkReport report)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Performance benchmark — {report.MachineName} — {report.TimestampUtc:u}");
        sb.AppendLine($"Corpus: {report.CorpusDescription}");
        sb.AppendLine(report.AssertThresholds
            ? "Mode: ASSERT (reference machine — absolute §9 thresholds enforced)"
            : "Mode: REPORT-ONLY (not a reference machine — numbers informational, exit 0)");
        sb.AppendLine();
        sb.AppendLine($"{"Operation",-40} {"Target",9} {"Stat",6} {"P50",9} {"P95",9} {"P99",9} {"Max",9}  Verdict");
        sb.AppendLine(new string('-', 110));

        foreach (var r in report.Results)
        {
            var s = r.Summary;
            var verdict = !report.AssertThresholds ? "—" : (r.MeetsTarget ? "PASS" : "FAIL");
            sb.AppendLine(
                $"{Trunc(r.Target.Operation, 40),-40} " +
                $"{r.Target.TargetMs,7:0}ms {r.MeasuredStatistic,6} " +
                $"{s.P50Ms,7:0.0}ms {s.P95Ms,7:0.0}ms {s.P99Ms,7:0.0}ms {s.MaxMs,7:0.0}ms  {verdict}");
        }

        if (report.Skipped is { Count: > 0 })
        {
            sb.AppendLine();
            sb.AppendLine("Skipped (not measured):");
            foreach (var s in report.Skipped)
                sb.AppendLine($"  {s.Target.Operation,-40} — {s.Reason}");
        }

        sb.AppendLine();
        if (report.AssertThresholds)
            sb.AppendLine(report.AllPassed ? "RESULT: all targets met." : "RESULT: one or more targets MISSED.");
        return sb.ToString();
    }

    private static string Trunc(string s, int max) => s.Length <= max ? s : s[..(max - 1)] + "…";
}

/// <summary>Renders a <see cref="BenchmarkReport"/> as JSON (the Layer 3 baseline format).</summary>
public static class JsonReporter
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static string Render(BenchmarkReport report) => JsonSerializer.Serialize(report, Options);
}
