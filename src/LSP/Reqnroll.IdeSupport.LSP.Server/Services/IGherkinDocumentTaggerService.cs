using OmniSharp.Extensions.LanguageServer.Protocol;
using Reqnroll.IdeSupport.LSP.Core.Editor.Services.Parsing.GherkinDocuments;

namespace Reqnroll.IdeSupport.LSP.Server.Services
{
    public interface IGherkinDocumentTaggerService
    {
        Task<IReadOnlyCollection<DeveroomTag>> ParseAsync(DocumentUri uri, int? version);
    }
}
