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
using Reqnroll.IdeSupport.LSP.Core.Gherkin.Parsing;
using Reqnroll.IdeSupport.LSP.Core.Matching;
using Reqnroll.IdeSupport.LSP.Server.Diagnostics;
using Reqnroll.IdeSupport.LSP.Server.Discovery;
using Reqnroll.IdeSupport.LSP.Core.Editor.Completions;
using Reqnroll.IdeSupport.LSP.Core.Editor.Completions.Matching;
using Reqnroll.IdeSupport.LSP.Core.Editor.Scaffolding;
using Reqnroll.IdeSupport.LSP.Core.Editor.Services.DocumentOutline;
using Reqnroll.IdeSupport.Common.Configuration;
using Reqnroll.IdeSupport.LSP.Server.Configuration;
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

        // Configure Dependency Injection
        options.Services
            // AddMediatR scans the assembly containing typeof(Program) and registers 
            // all INotificationHandler<T> implementations as transient services.
            // DO NOT add explicit AddSingleton<INotificationHandler<T>> registrations in 
            // AddReqnrollLspHandlers(), as it will cause MediatR to dispatch every 
            // notification to two handler instances (the transient from the scan and 
            // the singleton from the explicit call).
            .AddMediatR(typeof(Program).Assembly)
            .AddReqnrollLspCoreServices(clientIde)
            .AddReqnrollProjectSystem()
            .AddReqnrollEditorServices()
            .AddReqnrollLspHandlers();

        // Register standard LSP handlers
        options.AddStandardHandlers();

        // Initialize workspace scopes and custom protocol routing
        options.InitializeCustomProtocolRouting();

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
    }
}
