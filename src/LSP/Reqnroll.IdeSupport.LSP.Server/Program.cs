using MediatR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using OmniSharp.Extensions.LanguageServer.Server;
using Reqnroll.IdeSupport.Common;
using Reqnroll.IdeSupport.Common.Diagnostics;
using Reqnroll.IdeSupport.Common.ProjectSystem.Configuration;
using Reqnroll.IdeSupport.LSP.Core.Discovery;
using Reqnroll.IdeSupport.LSP.Core.Editor.Services.Parsing.GherkinDocuments;
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
        var server = await LanguageServer.From(ConfigureServer).ConfigureAwait(false);
        await server.WaitForExit.ConfigureAwait(false);
    }

    private static void ConfigureServer(LanguageServerOptions options)
    {
        options.WithInput(Console.OpenStandardInput())
               .WithOutput(Console.OpenStandardOutput());

        options.ConfigureLogging(logging =>
        {
            logging.SetMinimumLevel(LogLevel.Trace);
            logging.AddLanguageProtocolLogging();
        });

        options.WithServerInfo(new ServerInfo
        {
            Name    = "Reqnroll Language Server",
            Version = "0.1.0"
        });

        options.Services
               .AddMediatR(typeof(Program))
               .AddSingleton<IDeveroomLogger,                    LspDeveroomLogger>()
               .AddSingleton<IIdeScope,                          LspIdeScope>()
               .AddSingleton<IMonitoringService>(sp => NullMonitoringService.Instance)
               .AddSingleton<IDeveroomConfigurationProvider,     ProjectSystemDeveroomConfigurationProvider>()
               .AddSingleton<ILspWorkspaceScopeManager,          LspWorkspaceScopeManager>()
               .AddSingleton<IBindingRegistryProvider,           NullBindingRegistryProvider>()
               .AddSingleton<IDeveroomTagParser,                 DeveroomTagParser>()
               .AddSingleton<IDocumentBufferService,             DocumentBufferService>()
               .AddSingleton<IGherkinDocumentTaggerService,      GherkinDocumentTaggerService>()
               .AddSingleton<ISemanticTokenService,              SemanticTokenService>()
               // GherkinDocumentParsedNotificationHandler is registered both as itself (singleton)
               // and as the MediatR INotificationHandler so the same instance handles all notifications.
               .AddSingleton<GherkinDocumentParsedNotificationHandler>()
               .AddSingleton<INotificationHandler<GherkinDocumentParsedNotification>>(
                   sp => sp.GetRequiredService<GherkinDocumentParsedNotificationHandler>())
               // Handlers must be pre-registered as singletons so DryIoc can resolve
               // them without an open scope (TrackingDisposableTransients rule).
               .AddSingleton<TextDocumentSyncHandler>()
               .AddSingleton<WorkspaceFoldersHandler>()
               .AddSingleton<SemanticTokensHandler>();

        options.AddHandler<TextDocumentSyncHandler>()
               .AddHandler<WorkspaceFoldersHandler>()
               .AddHandler<SemanticTokensHandler>();

        options.OnStarted((languageServer, ct) =>
        {
            // Seed workspace scopes from the folders sent during the initialize handshake.
            var scopeManager = languageServer.Services.GetRequiredService<ILspWorkspaceScopeManager>();
            if (languageServer.ClientSettings.WorkspaceFolders != null)
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
    }
}
