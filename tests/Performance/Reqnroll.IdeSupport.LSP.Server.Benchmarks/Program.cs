#nullable enable

using System;
using System.IO;
using System.Threading.Tasks;
using Reqnroll.IdeSupport.LSP.Server.Benchmarks.Corpus;

namespace Reqnroll.IdeSupport.LSP.Server.Benchmarks;

/// <summary>
/// Entry point for the §9 Performance Verification Layer 2 tooling.
/// <list type="bullet">
///   <item><c>generate-corpus</c> — (re)generate the committed corpus and rewrite its manifest.</item>
///   <item>(default) — run the benchmark suite against the committed corpus.</item>
/// </list>
/// </summary>
public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        var command = args.Length > 0 ? args[0] : "run";
        switch (command)
        {
            case "generate-corpus":
                return await GenerateCorpusAsync(args).ConfigureAwait(false);
            case "run":
                return await BenchmarkRunner.RunAsync(args).ConfigureAwait(false);
            default:
                Console.Error.WriteLine($"Unknown command '{command}'. Use 'generate-corpus' or 'run'.");
                return 2;
        }
    }

    private static async Task<int> GenerateCorpusAsync(string[] args)
    {
        var corpusRoot = TryGetCorpusRoot() ?? DefaultCorpusRoot();
        Directory.CreateDirectory(corpusRoot);

        var generator = new CorpusGenerator();
        Console.WriteLine($"Generating corpus at {corpusRoot} " +
            $"({generator.FeatureFileCount} features, {generator.UniquePatternCount} unique patterns)...");
        generator.Generate(corpusRoot);

        var fingerprint = await CorpusFingerprint.ComputeAsync(corpusRoot).ConfigureAwait(false);
        var manifest = new CorpusManifest(
            Description: "Synthetic benchmark corpus for §9 Performance Verification (Layer 2 / T2). " +
                         "Pinned by the committed files; this manifest records the structural fingerprint.",
            Generator: new GeneratorParameters(
                generator.FeatureFileCount, generator.UniquePatternCount, generator.ScenariosPerFeature),
            Fingerprint: fingerprint);

        File.WriteAllText(CorpusLocator.ManifestPath(corpusRoot), manifest.ToJson() + Environment.NewLine);

        Console.WriteLine("Corpus regenerated. Fingerprint:");
        Console.WriteLine($"  feature files : {fingerprint.FeatureFileCount}");
        Console.WriteLine($"  scenarios     : {fingerprint.ScenarioCount} (+ {fingerprint.ScenarioOutlineCount} outlines)");
        Console.WriteLine($"  steps         : {fingerprint.StepCount}");
        Console.WriteLine($"  patterns      : {fingerprint.StepDefinitionPatternCount}");
        Console.WriteLine($"  bound         : {fingerprint.BoundStepCount}");
        Console.WriteLine($"  unbound       : {fingerprint.UnboundStepCount}");
        Console.WriteLine($"  ambiguous     : {fingerprint.AmbiguousStepCount}");
        return 0;
    }

    private static string? TryGetCorpusRoot()
    {
        try { return CorpusLocator.FindCorpusRoot(); }
        catch (DirectoryNotFoundException) { return null; }
    }

    // When the corpus does not yet exist (first generation), fall back to walking up to the repo
    // root from the assembly location and creating tests/Performance/Corpus there.
    private static string DefaultCorpusRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "tests", "Performance")))
            dir = dir.Parent;
        var baseDir = dir?.FullName ?? Directory.GetCurrentDirectory();
        return Path.Combine(baseDir, "tests", "Performance", "Corpus");
    }
}
