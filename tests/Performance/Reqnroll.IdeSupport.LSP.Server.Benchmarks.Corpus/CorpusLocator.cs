#nullable enable

using System;
using System.IO;

namespace Reqnroll.IdeSupport.LSP.Server.Benchmarks.Corpus;

/// <summary>
/// Locates the committed benchmark corpus (<c>tests/Performance/Corpus</c>) by walking up from a
/// starting directory until the corpus folder is found. Used by both the benchmark driver and the
/// drift test, which run from different <c>bin</c> output directories.
/// </summary>
public static class CorpusLocator
{
    public const string CorpusRelativePath = "tests/Performance/Corpus";
    public const string ManifestFileName = "corpus.manifest.json";

    /// <summary>
    /// Walks up from <paramref name="startDir"/> (default: the running assembly's base directory)
    /// looking for <c>tests/Performance/Corpus</c>. Throws if not found.
    /// </summary>
    public static string FindCorpusRoot(string? startDir = null)
    {
        var dir = new DirectoryInfo(startDir ?? AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, CorpusRelativePath.Replace('/', Path.DirectorySeparatorChar));
            if (Directory.Exists(candidate))
                return candidate;
            dir = dir.Parent;
        }

        throw new DirectoryNotFoundException(
            $"Could not locate '{CorpusRelativePath}' walking up from '{startDir ?? AppContext.BaseDirectory}'.");
    }

    public static string ManifestPath(string corpusRoot) => Path.Combine(corpusRoot, ManifestFileName);
}
