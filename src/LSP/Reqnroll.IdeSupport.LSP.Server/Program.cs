using MediatR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using OmniSharp.Extensions.LanguageServer.Protocol.Client.Capabilities;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using OmniSharp.Extensions.LanguageServer.Server;
using Reqnroll.IdeSupport.Common;
using Reqnroll.IdeSupport.Common.Diagnostics;
using Reqnroll.IdeSupport.Common.ProjectSystem.Configuration;
using Reqnroll.IdeSupport.LSP.Server.Protocol;
using Reqnroll.IdeSupport.LSP.Core.Diagnostics;
using Reqnroll.IdeSupport.LSP.Core.Editor.Services.Parsing.GherkinDocuments;
using Reqnroll.IdeSupport.LSP.Core.Matching;
using Reqnroll.IdeSupport.LSP.Server.Diagnostics;
using Reqnroll.IdeSupport.LSP.Server.Discovery;
using Reqnroll.IdeSupport.LSP.Server.Handlers.InternalHandlers;
using Reqnroll.IdeSupport.LSP.Server.Handlers.ProtocolHandlers;
using Reqnroll.IdeSupport.LSP.Server.Notifications;
using Reqnroll.IdeSupport.LSP.Server.Services;
using Reqnroll.IdeSupport.LSP.Server.Workspace;

namespace Reqnroll.IdeSupport.LSP.Server;

public class Program
{
    public static async Task Main(string[] args)
    {
        // Each IDE's glue component passes --ide <identifier> when spawning the server.
        // The semantic token legend no longer varies by IDE, but the identifier is retained for
        // features that may need to vary their behaviour per IDE (e.g. future static-vs-dynamic
        // capability registration decisions).
        var ideId = args
            .SkipWhile(a => !string.Equals(a, "--ide", StringComparison.OrdinalIgnoreCase))
            .Skip(1)
            .FirstOrDefault();

        // Write any unhandled startup exception to a file next to the LSP inspector logs
        // so crashes are self-diagnosing without needing to capture stderr.
        try
        {
            var server = await LanguageServer
                .From(options =>
                {
                    // Production transport: the IDE talks to the server over stdio.
                    options.WithInput(Console.OpenStandardInput())
                           .WithOutput(Console.OpenStandardOutput());
                    ConfigureServer(options, ideId);
                })
                .ConfigureAwait(false);
            await server.WaitForExit.ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            try
            {
                var logDir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "Reqnroll");
                Directory.CreateDirectory(logDir);
                var logPath = Path.Combine(logDir,
                    $"lsp-server-crash-{DateTime.Now:yyyyMMdd-HHmmss}.txt");
                File.WriteAllText(logPath, ex.ToString());
            }
            catch { /* best-effort; never mask the original exception */ }
            throw;
        }
    }

    /// <summary>
    /// Applies the full server configuration (logging, DI graph, capabilities, custom
    /// notifications) to <paramref name="options"/>.  The transport (input/output) is
    /// intentionally NOT set here so that callers can choose it: production uses stdio
    /// (see <see cref="Main"/>); the in-process protocol specs host the server over an
    /// in-memory pipe.
    /// </summary>
    /// <param name="clientIde">
    /// The <c>--ide</c> identifier of the connecting client (e.g. <c>"visualstudio"</c>), or
    /// <see langword="null"/> when absent.  Currently unused by the semantic-token pipeline
    /// (the legend is shared across IDEs); retained for features that may vary behaviour per IDE.
    /// </param>
    internal static void ConfigureServer(LanguageServerOptions options, string? clientIde = null)
    {
        IServiceProvider? serverServices = null;

        options.ConfigureLogging(logging =>
        {
            logging.SetMinimumLevel(LogLevel.Trace);
            logging.AddLanguageProtocolLogging();
        });

        options.WithServerInfo(new ServerInfo
        {
            Name = "Reqnroll Language Server",
            Version = "0.1.0"
        });

        options.Services
               .AddMediatR(typeof(Program))
               // The connecting client's --ide identifier, so handlers can vary behaviour per IDE
               // (e.g. SemanticTokensPushHandler pushes tokens to Visual Studio).
               .AddSingleton(new ClientIdeContext(clientIde))
               .AddSingleton<IDeveroomLogger, LspDeveroomLogger>()
               .AddSingleton<IIdeScope, LspIdeScope>()
               .AddSingleton<IMonitoringService>(sp => NullMonitoringService.Instance)
               .AddSingleton<IDeveroomConfigurationProvider, ProjectSystemDeveroomConfigurationProvider>()
               .AddSingleton<ILspWorkspaceScopeManager, LspWorkspaceScopeManager>()
               // BindingRegistryProviderRouter creates and owns one ConnectorBindingRegistryProvider
               // per project and routes binding-registry lookups to the correct per-project instance
               // via IProjectBindingRegistryLookup.GetRegistryForUri.  Registries are NOT merged —
               // each feature file is resolved against only its own project's bindings.
               .AddSingleton<BindingRegistryProviderRouter>()
               .AddSingleton<IProjectBindingRegistryLookup>(sp => sp.GetRequiredService<BindingRegistryProviderRouter>())
               // Roslyn (source-level) binding discovery for .cs edits (design doc F2).
               .AddSingleton<ICSharpBindingDiscoveryService, CSharpBindingDiscoveryService>()
               .AddSingleton<IDeveroomTagParser, DeveroomTagParser>()
               .AddSingleton<IDocumentBufferService, DocumentBufferService>()
               // BindingMatchService holds the per-document match cache; it must be a singleton
               // so the cache survives across requests and is shared by the tagger (writer) and
               // the Go to Definition / diagnostics consumers (readers).
               .AddSingleton<IBindingMatchService, BindingMatchService>()
               .AddSingleton<IGherkinDocumentTaggerService, GherkinDocumentTaggerService>()
               .AddSingleton<ISemanticTokenService, SemanticTokenService>()
               .AddSingleton<IDiagnosticsAggregator, DiagnosticsAggregator>()
               // MediatR notification handlers — registered solely via AddMediatR(typeof(Program))
               // above, which scans this assembly and registers each INotificationHandler<T>
               // implementation exactly once as a transient service.  Do NOT add explicit
               // AddSingleton<INotificationHandler<T>> registrations here: that would create a
               // second registration for the same interface, causing MediatR to dispatch every
               // notification to two handler instances (the transient from the scan and the
               // singleton from the explicit call).  None of these handlers are IDisposable, so
               // DryIoc's TrackingDisposableTransients validation does not require them to be
               // singletons.  Handlers covered: SemanticTokensRefreshHandler,
               // SemanticTokensPushHandler, DiagnosticsPublishHandler,
               // ReqnrollConfigChangedHandler, BindingRegistryChangedHandler.
               //
               // OmniSharp protocol handlers ARE registered as singletons below because OmniSharp
               // resolves them from the root DryIoc container (not from a per-request scope) and
               // they hold injected ILanguageServer references that must live for the server's
               // lifetime.
               .AddSingleton<TextDocumentSyncHandler>()
               .AddSingleton<WorkspaceFoldersHandler>()
               .AddSingleton<WatchedFilesHandler>()
               .AddSingleton<SemanticTokensHandler>()
               .AddSingleton<StepReferencesHandler>()
               .AddSingleton<FindStepUsagesHandler>()
               .AddSingleton<FeatureDefinitionHandler>();

        options.AddHandler<TextDocumentSyncHandler>()
               .AddHandler<WorkspaceFoldersHandler>()
               .AddHandler<WatchedFilesHandler>()
               .AddHandler<FeatureDefinitionHandler>();

        options.OnStarted((languageServer, ct) =>
        {
            // Seed workspace scopes from the folders sent during the initialize handshake.
            var scopeManager = languageServer.Services.GetRequiredService<ILspWorkspaceScopeManager>();
            serverServices = languageServer.Services; // Caching the service provider for later use in handlers that don't have it injected.
            if (languageServer.ClientSettings.WorkspaceFolders is not null)
            {
                foreach (var folder in languageServer.ClientSettings.WorkspaceFolders)
                {
                    var path = folder.Uri.GetFileSystemPath();
                    if (!string.IsNullOrEmpty(path))
                        scopeManager.OpenWorkspace(path);
                }
            }

            return Task.CompletedTask;
        });
        options.OnInitialized((languageServer, request, response, ct) =>
        {
            var tokenService = languageServer.Services.GetRequiredService<ISemanticTokenService>();

            response.Capabilities.SemanticTokensProvider = new SemanticTokensRegistrationOptions.StaticOptions
            {
                Legend = tokenService.Legend,
                Full = true,
                Range = false
            };

            return Task.CompletedTask;
        });

        // ── Custom client-to-server notifications ─────────────────────────────
        // reqnroll/projectLoaded — IDE glue sends project metadata when a project opens or rebuilds.
        options.OnNotification<ReqnrollProjectLoadedParams>(
            "reqnroll/projectLoaded",
            (p, ct) => serverServices!
                .GetRequiredService<ILspWorkspaceScopeManager>()
                .HandleProjectLoadedAsync(p, ct));

        // reqnroll/projectUnloaded — IDE glue sends this when a project is removed.
        options.OnNotification<ReqnrollProjectUnloadedParams>(
            "reqnroll/projectUnloaded",
            (p, ct) => serverServices!
                .GetRequiredService<ILspWorkspaceScopeManager>()
                .HandleProjectUnloadedAsync(p, ct));

        // reqnroll/projectFiles — IDE glue sends the authoritative file-membership index
        // (baseline on load/rebuild, delta on item add/remove).  Drives I1/I2 invariants.
        options.OnNotification<ReqnrollProjectFilesParams>(
            "reqnroll/projectFiles",
            (p, ct) => serverServices!
                .GetRequiredService<ILspWorkspaceScopeManager>()
                .HandleProjectFilesAsync(p, ct));

        // ── Bypassing registering the SemanticTokensHandler as a regular handler ──
        // Otherwise, it will register its capabilities dynamically, which VisualStudio doesn't support.
        // Directly wiring the handler to respond to specific request messages.
        options.OnRequest<SemanticTokensParams, SemanticTokens?>(
                    "textDocument/semanticTokens/full",
                    (request, ct) => serverServices!.GetRequiredService<SemanticTokensHandler>().HandleAsync(request, ct));
        options.OnRequest<SemanticTokensDeltaParams, SemanticTokensFullOrDelta?>(
                    "textDocument/semanticTokens/full/delta",
                    (request, ct) => serverServices!.GetRequiredService<SemanticTokensHandler>().HandleAsync(request, ct));

        // F14 — Find Step Definition Usages.
        // Registered manually (same pattern as semantic tokens) to avoid dynamic registration
        // ambiguity with the C# language server on .cs files. See design-doc Q13.
        options.OnRequest<ReferenceParams, LocationOrLocationLinks?>(
            "textDocument/references",
            (request, ct) => serverServices!.GetRequiredService<StepReferencesHandler>().HandleAsync(request, ct));

        // F14 P2b — Custom reqnroll/findStepUsages request.
        // Delivers the full three-state contract (null / empty / locations) and per-location stepText
        // that textDocument/references cannot carry (OmniSharp LocationOrLocationLinks cannot serialize null).
        // The VS client uses this request exclusively; textDocument/references is retained for
        // spec-test compatibility and any future non-VS clients.
        options.OnRequest<ReferenceParams, FindStepUsagesResponse?>(
            "reqnroll/findStepUsages",
            (request, ct) => serverServices!.GetRequiredService<FindStepUsagesHandler>().HandleAsync(request, ct));

    }
}
