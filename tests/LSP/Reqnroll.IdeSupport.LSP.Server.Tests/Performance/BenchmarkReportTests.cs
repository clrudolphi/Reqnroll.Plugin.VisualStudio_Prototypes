#nullable enable

using System;
using System.Collections.Generic;
using Reqnroll.IdeSupport.LSP.Server.Benchmarks.Latency;
using Reqnroll.IdeSupport.LSP.Server.Benchmarks.Reporting;
using Reqnroll.IdeSupport.LSP.Server.Benchmarks.Scenarios;

namespace Reqnroll.IdeSupport.LSP.Server.Tests.Performance;

public class BenchmarkReportTests
{
    private static BenchmarkReport MakeReport(bool assert, double definitionP95) =>
        new(
            MachineName: "TESTBOX",
            AssertThresholds: assert,
            TimestampUtc: DateTimeOffset.UnixEpoch,
            CorpusDescription: "50 features, 64 patterns, 1350 steps",
            Results: new List<OperationResult>
            {
                new(PerfTargets.DefinitionCacheHit,
                    new LatencySummary("textDocument/definition", 100, 1, 5, 5, definitionP95, definitionP95, definitionP95)),
            });

    [Fact]
    public void Console_table_lists_operations_and_marks_verdicts_when_asserting()
    {
        var table = MakeReport(assert: true, definitionP95: 90).ToConsoleTable();

        table.Should().Contain("textDocument/definition");
        table.Should().Contain("ASSERT");
        table.Should().Contain("PASS");
        table.Should().Contain("RESULT: all targets met.");
    }

    [Fact]
    public void Console_table_is_report_only_and_hides_verdicts_when_not_asserting()
    {
        var table = MakeReport(assert: false, definitionP95: 9999).ToConsoleTable();

        table.Should().Contain("REPORT-ONLY");
        table.Should().NotContain("FAIL");
    }

    [Fact]
    public void AllPassed_reflects_the_measured_statistic_against_target()
    {
        MakeReport(assert: true, definitionP95: 90).AllPassed.Should().BeTrue();
        MakeReport(assert: true, definitionP95: 150).AllPassed.Should().BeFalse();
    }

    [Fact]
    public void Json_contains_operation_and_percentiles()
    {
        var json = MakeReport(assert: true, definitionP95: 90).ToJson();

        json.Should().Contain("textDocument/definition");
        json.Should().Contain("P95Ms");
        json.Should().Contain("TESTBOX");
    }

    [Fact]
    public void Console_table_lists_skipped_scenarios_with_their_reason()
    {
        var report = MakeReport(assert: false, definitionP95: 5) with
        {
            Skipped = new[]
            {
                new SkippedBatchScenario(PerfTargets.ReflectionDiscovery, "requires a built corpus bindings assembly"),
            },
        };

        var table = report.ToConsoleTable();
        table.Should().Contain("Skipped (not measured):");
        table.Should().Contain("discovery/reflection-post-build");
        table.Should().Contain("requires a built corpus bindings assembly");
    }
}
