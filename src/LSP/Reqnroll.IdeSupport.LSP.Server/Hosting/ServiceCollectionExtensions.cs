using MediatR;
using Microsoft.Extensions.DependencyInjection;
using Reqnroll.IdeSupport.Common;
using Reqnroll.IdeSupport.Common.Configuration;
using Reqnroll.IdeSupport.Common.Diagnostics;
using Reqnroll.IdeSupport.Common.ProjectSystem.Configuration;
using Reqnroll.IdeSupport.LSP.Core.Diagnostics;
using Reqnroll.IdeSupport.LSP.Core.Editor.Completions;
using Reqnroll.IdeSupport.LSP.Core.Editor.Completions.Matching;
using Reqnroll.IdeSupport.LSP.Core.Editor.Scaffolding;
using Reqnroll.IdeSupport.LSP.Core.Editor.Services.DocumentOutline;
using Reqnroll.IdeSupport.LSP.Core.Editor.Services.Folding;
using Reqnroll.IdeSupport.LSP.Core.Editor.Services.Commenting;
using Reqnroll.IdeSupport.LSP.Core.Gherkin.Parsing;
using Reqnroll.IdeSupport.LSP.Core.Matching;
using Reqnroll.IdeSupport.LSP.Core.Rename;
using Reqnroll.IdeSupport.LSP.Server.Configuration;
using Reqnroll.IdeSupport.LSP.Server.Logging;
using Reqnroll.IdeSupport.LSP.Server.Discovery;
using Reqnroll.IdeSupport.LSP.Server.Handlers.InternalHandlers;
using Reqnroll.IdeSupport.LSP.Server.Handlers.ProtocolHandlers;
using Reqnroll.IdeSupport.LSP.Server.Services;
using Reqnroll.IdeSupport.LSP.Server.Workspace;

namespace Reqnroll.IdeSupport.LSP.Server.Hosting;

/// <summary>
/// Extension methods for configuring the Dependency Injection container for the Reqnroll LSP Server.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers core infrastructure and cross-cutting services.
    /// </summary>
    public static IServiceCollection AddReqnrollLspCoreServices(this IServiceCollection services, string? clientIde)
    {
        return services
            .AddSingleton(new ClientIdeContext(clientIde))
            .AddSingleton<IDeveroomLogger, LspDeveroomLogger>()
            .AddSingleton<IIdeScope, LspIdeScope>()
            .AddSingleton<IMonitoringService>(sp => NullMonitoringService.Instance)
            .AddSingleton<ILspTelemetryService, LspTelemetryService>()
            .AddSingleton<IDeveroomConfigurationProvider, ProjectSystemDeveroomConfigurationProvider>()
            .AddSingleton<IEditorConfigOptionsProvider>(sp =>
                new FileSystemEditorConfigOptionsProvider(sp.GetRequiredService<IIdeScope>().FileSystem));
    }

    /// <summary>
    /// Registers project system, workspace, and binding discovery services.
    /// </summary>
    public static IServiceCollection AddReqnrollProjectSystem(this IServiceCollection services)
    {
        return services
            .AddSingleton<ILspWorkspaceScopeManager, LspWorkspaceScopeManager>()
            // BindingRegistryProviderRouter creates and owns one ConnectorBindingRegistryProvider
            // per project and routes binding-registry lookups to the correct per-project instance
            // via IProjectBindingRegistryLookup.GetRegistryForUri. Registries are NOT merged —
            // each feature file is resolved against only its own project's bindings.
            .AddSingleton<BindingRegistryProviderRouter>()
            .AddSingleton<IProjectBindingRegistryLookup>(sp => sp.GetRequiredService<BindingRegistryProviderRouter>())
            // Roslyn (source-level) binding discovery for .cs edits (design doc F2).
            .AddSingleton<ICSharpBindingDiscoveryService, CSharpBindingDiscoveryService>()
            .AddSingleton<IDeveroomTagParser, DeveroomTagParser>();
    }

    /// <summary>
    /// Registers editor services, parsing, and shared caching components.
    /// </summary>
    public static IServiceCollection AddReqnrollEditorServices(this IServiceCollection services)
    {
        return services
            .AddSingleton<IDocumentBufferService, DocumentBufferService>()
            // BindingMatchService holds the per-document match cache; it must be a singleton
            // so the cache survives across requests and is shared by the tagger (writer) and
            // the Go to Definition / diagnostics consumers (readers).
            .AddSingleton<IBindingMatchService, BindingMatchService>()
            .AddSingleton<IGherkinDocumentTaggerService, GherkinDocumentTaggerService>()
            .AddSingleton<ISemanticTokenService, SemanticTokenService>()
            .AddSingleton<IDiagnosticsAggregator, DiagnosticsAggregator>()
            .AddSingleton<IGherkinFoldingRangeService, GherkinFoldingRangeService>()
            .AddSingleton<ICommentToggleService, CommentToggleService>();
    }

    /// <summary>
    /// Registers OmniSharp LSP protocol handlers as singletons.
    /// 
    /// NOTE: MediatR notification handlers (e.g., SemanticTokensRefreshHandler, DiagnosticsPublishHandler) 
    /// are auto-discovered and registered as transients by the AddMediatR call in ConfigureServer. 
    /// DO NOT add explicit AddSingleton&lt;INotificationHandler&lt;T&gt;&gt; registrations here, as it will 
    /// cause MediatR to dispatch every notification to two handler instances (the transient from the 
    /// scan and the singleton from the explicit call). 
    /// 
    /// The handlers listed below ARE registered explicitly as singletons because OmniSharp resolves 
    /// them from the root DryIoc container (not from a per-request scope), and they hold injected 
    /// ILanguageServer references that must live for the server's lifetime.
    /// </summary>
    public static IServiceCollection AddReqnrollLspHandlers(this IServiceCollection services)
    {
        return services
            .AddSingleton<TextDocumentSyncHandler>()
            .AddSingleton<WorkspaceFoldersHandler>()
            .AddSingleton<WatchedFilesHandler>()
            .AddSingleton<SemanticTokensHandler>()
            .AddSingleton<StepReferencesHandler>()
            .AddSingleton<FindStepUsagesHandler>()
            .AddSingleton<ICompletionContextResolver, CompletionContextResolver>()
            .AddSingleton<ICompletionService, CompletionService>()
            .AddSingleton<ICompletionMatcher, ReturnAllCompletionMatcher>()
            .AddSingleton<FeatureDefinitionHandler>()
            .AddSingleton<GoToStepDefinitionsHandler>()
            .AddSingleton<GoToHooksHandler>()
            .AddSingleton<StepCodeLensHandler>()
            .AddSingleton<GherkinCompletionHandler>()
            .AddSingleton<IStepScaffoldService, StepScaffoldService>()
            .AddSingleton<FeatureCodeActionHandler>()
            .AddSingleton<FindUnusedStepDefinitionsHandler>()
            .AddSingleton<GherkinFormattingHandler>()
            .AddSingleton<IGherkinDocumentSymbolService, GherkinDocumentSymbolService>()
            .AddSingleton<FeatureDocumentSymbolHandler>()
            .AddSingleton<FeatureFoldingRangeHandler>()
            .AddSingleton<CommentToggleHandler>()
            .AddSingleton<StepRenameHandler>()
            .AddSingleton<RenameSessionManager>();
    }
}
