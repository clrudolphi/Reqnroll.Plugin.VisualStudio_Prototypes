using OmniSharp.Extensions.LanguageServer.Protocol;
using Reqnroll.IdeSupport.LSP.Core.Editor.Services.Parsing.GherkinDocuments;
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
    }
}
