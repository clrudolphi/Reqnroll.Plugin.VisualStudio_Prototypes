#nullable enable

using System.Threading.Tasks;
using Reqnroll.IdeSupport.LSP.Server.Benchmarks.Corpus;

namespace Reqnroll.IdeSupport.LSP.Server.Tests.Performance;

/// <summary>
/// Corpus drift test — the <b>pin</b> for the §9 Performance Verification corpus (T2).
/// <para>
/// Re-derives the structural fingerprint from the <b>committed</b> corpus (parse + binding
/// discovery + match) and asserts it equals the fingerprint stored in <c>corpus.manifest.json</c>.
/// Fails if the checked-in corpus changes <em>size or shape</em> — a feature/scenario/step or
/// binding pattern added or removed, or a step edited such that its match status flips
/// (bound ⇄ unbound ⇄ ambiguous). It tolerates step-text/whitespace edits that don't move a count.
/// </para>
/// <para>
/// It does <b>not</b> regenerate the corpus (regeneration is not byte-stable — see the
/// implementation plan, A3.2.1) and does <b>not</b> hash bytes.
/// </para>
/// </summary>
public class CorpusDriftTests
{
    [Fact]
    public async Task Committed_corpus_matches_the_pinned_structural_fingerprint()
    {
        var corpusRoot = CorpusLocator.FindCorpusRoot();
        var manifest = CorpusManifest.Load(CorpusLocator.ManifestPath(corpusRoot));

        var actual = await CorpusFingerprint.ComputeAsync(corpusRoot);

        actual.Should().Be(manifest.Fingerprint,
            "the committed corpus must match its pinned fingerprint; if this corpus change is " +
            "intentional, regenerate via 'generate-corpus' to re-pin the manifest.");
    }

    [Fact]
    public async Task Corpus_stays_within_the_section_9_envelope()
    {
        var corpusRoot = CorpusLocator.FindCorpusRoot();
        var fp = await CorpusFingerprint.ComputeAsync(corpusRoot);

        // §9 "typical workspace conditions": <=500 feature files, <=2,000 binding patterns.
        fp.FeatureFileCount.Should().BeLessThanOrEqualTo(500);
        fp.StepDefinitionPatternCount.Should().BeLessThanOrEqualTo(2000);

        // The corpus must exercise each match path, not be degenerate all-green/all-red.
        fp.BoundStepCount.Should().BeGreaterThan(0);
        fp.UnboundStepCount.Should().BeGreaterThan(0);
        fp.AmbiguousStepCount.Should().BeGreaterThan(0);
    }
}
