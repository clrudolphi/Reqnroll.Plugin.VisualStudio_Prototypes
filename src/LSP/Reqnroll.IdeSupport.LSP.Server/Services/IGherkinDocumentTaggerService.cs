using OmniSharp.Extensions.LanguageServer.Protocol;
using Reqnroll.IdeSupport.LSP.Core.Gherkin.Parsing;
using Reqnroll.IdeSupport.LSP.Server.Workspace;

namespace Reqnroll.IdeSupport.LSP.Server.Services
{
    public interface IGherkinDocumentTaggerService
    {
        Task<IReadOnlyCollection<DeveroomTag>> ParseAsync(DocumentUri uri, int? version);

        /// <summary>
        /// Parses <paramref name="text"/> as the content of <paramref name="uri"/> using
        /// <paramref name="project"/>'s binding registry and stores the resulting match set
        /// keyed by <c>(uri, project)</c>.  The document buffer is not updated; open-file
        /// semantics are unaffected.
        /// Used for workspace-wide scans of feature files that are not currently open
        /// (e.g. the initial scan triggered on startup by a full binding-registry replacement).
        /// </summary>
        Task ScanClosedFileAsync(DocumentUri uri, string text, LspReqnrollProject project);

        /// <summary>
        /// Re-scans <paramref name="uri"/> from disk as a closed file for every project that owns
        /// it, repopulating the binding match cache. Called when a feature file is closed so its
        /// usages stay discoverable (Find Usages / Rename) after the open buffer is removed.
        /// No-op when the file is missing on disk or has no owning project.
        /// </summary>
        Task RescanClosedFileAsync(DocumentUri uri);
    }
}
