#nullable enable

using System;
using System.Threading.Tasks;

namespace Reqnroll.IdeSupport.LSP.Server.Benchmarks;

/// <summary>
/// Drives the §9 Layer 2 benchmark suite against the committed corpus and reports per-operation
/// latency percentiles. Fully populated in Phase 2–4 of the implementation plan.
/// </summary>
public static class BenchmarkRunner
{
    public static Task<int> RunAsync(string[] args)
    {
        Console.Error.WriteLine("Benchmark 'run' command is not yet wired. Use 'generate-corpus' for now.");
        return Task.FromResult(0);
    }
}
