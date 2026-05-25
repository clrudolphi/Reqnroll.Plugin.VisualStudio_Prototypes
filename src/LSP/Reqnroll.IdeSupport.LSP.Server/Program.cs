using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using OmniSharp.Extensions.LanguageServer.Server;
using Reqnroll.IdeSupport.Common.Diagnostics;
using Reqnroll.IdeSupport.Common.ProjectSystem.Configuration;
using Reqnroll.IdeSupport.LSP.Core.Discovery;
using Reqnroll.IdeSupport.LSP.Core.Editor.Services.Parsing.GherkinDocuments;
using Reqnroll.IdeSupport.LSP.Server.Diagnostics;
using Reqnroll.IdeSupport.LSP.Server.Discovery;
using Reqnroll.IdeSupport.LSP.Server.Handlers;
using Reqnroll.IdeSupport.LSP.Server.Services;

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
               .AddSingleton<IDeveroomLogger,                LspDeveroomLogger>()
               .AddSingleton<IDeveroomConfigurationProvider, LspDeveroomConfigurationProvider>()
               .AddSingleton<IBindingRegistryProvider,       NullBindingRegistryProvider>()
               .AddSingleton<IDeveroomTagParser,             DeveroomTagParser>()
               .AddSingleton<IDocumentBufferService,         DocumentBufferService>()
               .AddSingleton<IGherkinDocumentTaggerService,  GherkinDocumentTaggerService>()
               .AddSingleton<ISemanticTokenService,          SemanticTokenService>();

        options.AddHandler<TextDocumentSyncHandler>()
               .AddHandler<SemanticTokensHandler>();

        options.OnStarted((languageServer, ct) =>
        {
            _ = languageServer.Services.GetRequiredService<ISemanticTokenService>();
            return Task.CompletedTask;
        });
    }
}
