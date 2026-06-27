#nullable enable

using System.IO;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Reqnroll.IdeSupport.LSP.Server.Benchmarks.Corpus;

/// <summary>
/// The pinned descriptor of the committed corpus: its structural <see cref="CorpusFingerprint"/>
/// plus the generator parameters that produced it. The corpus drift test recomputes the
/// fingerprint from the committed files and asserts it equals <see cref="Fingerprint"/>.
/// </summary>
public sealed record CorpusManifest(
    string Description,
    GeneratorParameters Generator,
    CorpusFingerprint Fingerprint)
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        // Keep the committed manifest human-readable (don't \uXXXX-escape § and friends).
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public string ToJson() => JsonSerializer.Serialize(this, Options);

    public static CorpusManifest FromJson(string json) =>
        JsonSerializer.Deserialize<CorpusManifest>(json, Options)
        ?? throw new JsonException("corpus.manifest.json deserialized to null.");

    public static CorpusManifest Load(string manifestPath) =>
        FromJson(File.ReadAllText(manifestPath));
}

/// <summary>The generator inputs, recorded so the corpus can be regenerated for a deliberate re-pin.</summary>
public sealed record GeneratorParameters(
    int FeatureFileCount,
    int UniquePatternCount,
    int ScenariosPerFeature);
