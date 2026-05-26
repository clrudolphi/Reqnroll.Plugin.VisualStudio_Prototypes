using OmniSharp.Extensions.LanguageServer.Protocol;
using Reqnroll.IdeSupport.Common.Diagnostics;
using Reqnroll.IdeSupport.LSP.Core.Discovery;
using Reqnroll.IdeSupport.LSP.Core.Editor.Services.Parsing.GherkinDocuments;
using Reqnroll.IdeSupport.LSP.Server.Workspace;

namespace Reqnroll.IdeSupport.LSP.Server.Services;

public class GherkinDocumentTaggerService : IGherkinDocumentTaggerService
{
    private readonly IDeveroomTagParser _tagParser;
    private readonly IBindingRegistryProvider _bindingRegistryProvider;
    private readonly ILspWorkspaceScopeManager _scopeManager;
    private readonly IDeveroomLogger _logger;
    private readonly IDocumentBufferService _documentBufferService;

    public GherkinDocumentTaggerService(
        IDocumentBufferService documentBufferService,
        IDeveroomTagParser tagParser,
        IBindingRegistryProvider bindingRegistryProvider,
        ILspWorkspaceScopeManager scopeManager,
        IDeveroomLogger logger)
    {
        _documentBufferService = documentBufferService;
        _tagParser = tagParser;
        _bindingRegistryProvider = bindingRegistryProvider;
        _scopeManager = scopeManager;
        _logger = logger;
    }

    public Task<IReadOnlyCollection<DeveroomTag>> ParseAsync(DocumentUri uri, int? version)
    {
        if (!_documentBufferService.TryGet(uri, out var buffer))
            return Task.FromResult<IReadOnlyCollection<DeveroomTag>>(Array.Empty<DeveroomTag>());

        var snapshot = buffer?.ToGherkinTextSnapshot();

        if (snapshot == null)
            return Task.FromResult<IReadOnlyCollection<DeveroomTag>>(Array.Empty<DeveroomTag>());

        if (version.HasValue && snapshot.Version != version)
        {
            _logger.LogWarning($"Version mismatch for document {uri}: expected {version}, got {snapshot.Version}");
            return Task.FromResult<IReadOnlyCollection<DeveroomTag>>(Array.Empty<DeveroomTag>());
        }

        //var configuration = _scopeManager.GetConfigurationProviderForUri(uri).GetConfiguration();
        var tags = _tagParser.Parse(snapshot);
        _logger.LogInfo($"Parsed {tags.Count} tags from document {uri}");
        return Task.FromResult(tags);
    }
}
