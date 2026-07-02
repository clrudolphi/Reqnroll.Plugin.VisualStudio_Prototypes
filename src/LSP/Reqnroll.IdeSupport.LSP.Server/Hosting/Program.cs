using System.Diagnostics;
using MediatR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using OmniSharp.Extensions.LanguageServer.Protocol.Client.Capabilities;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using OmniSharp.Extensions.LanguageServer.Protocol.Server.Capabilities;
using OmniSharp.Extensions.LanguageServer.Server;
using Reqnroll.IdeSupport.Common;
using Reqnroll.IdeSupport.Common.Diagnostics;
using Reqnroll.IdeSupport.Common.ProjectSystem.Configuration;
using Reqnroll.IdeSupport.LSP.Server.Protocol;
using Reqnroll.IdeSupport.LSP.Core.Diagnostics;
using Reqnroll.IdeSupport.LSP.Core.Gherkin.Parsing;
using Reqnroll.IdeSupport.LSP.Core.Matching;
using Reqnroll.IdeSupport.LSP.Server.Logging;
using Reqnroll.IdeSupport.LSP.Server.Discovery;
using Reqnroll.IdeSupport.LSP.Core.Completions;
using Reqnroll.IdeSupport.LSP.Core.Completions.Matching;
using Reqnroll.IdeSupport.LSP.Core.Scaffolding;
using Reqnroll.IdeSupport.LSP.Core.DocumentOutline;
using Reqnroll.IdeSupport.Common.Configuration;
using Reqnroll.IdeSupport.LSP.Server.Configuration;
using Reqnroll.IdeSupport.LSP.Server.Pipeline;
using Reqnroll.IdeSupport.LSP.Server.Features.SemanticTokens;
using Reqnroll.IdeSupport.LSP.Server.Workspace;

namespace Reqnroll.IdeSupport.LSP.Server.Hosting;

public class Program
{
    public static async Task Main(string[] args)
    {
        // Each IDE's glue component passes --ide <identifier> when spawning the server.
        // The semantic token legend no longer varies by IDE, but the identifier is retained for
        // features that may need to vary their behaviour per IDE (e.g. future static-vs-dynamic
        // capability registration decisions).
        var ideId = ParseArg(args, "--ide");

        // Each IDE's glue component may pass --log-level <level> (Off/Error/Warning/Info/Verbose)
        // when spawning the server. Defaults to Warning when absent so a normal session doesn't
        // write maximum-verbosity logs indefinitely; pass --log-level Verbose for full tracing.
        var logLevel = ParseLogLevel(args);

        // Write any unhandled startup exception to a file next to the LSP inspector logs
        // so crashes are self-diagnosing without needing to capture stderr.
        try
        {
            // LanguageServer.PreInit (unlike .From) builds the DI container and constructs the
            // server WITHOUT blocking on the client's "initialize" handshake — .Services is usable
            // immediately. .From's await blocks inside Initialize() until a real client "initialize"
            // arrives, which would gate ProjectPreloadListener behind the exact thing it exists to
            // route around. See ProjectPreloadListener's remarks for the full rationale.
            var server = LanguageServer.PreInit(options =>
            {
                // Production transport: the IDE talks to the server over stdio.
                options.WithInput(Console.OpenStandardInput())
                       .WithOutput(Console.OpenStandardOutput());
                ConfigureServer(options, ideId, logLevel);
            });

            using var preloadCts = new CancellationTokenSource();
            var scopeManager = server.Services.GetRequiredService<ILspWorkspaceScopeManager>();
            var logger       = server.Services.GetRequiredService<IDeveroomLogger>();
            var preloadTask  = ProjectPreloadListener.RunAsync(scopeManager, logger, preloadCts.Token);

            await server.Initialize(CancellationToken.None).ConfigureAwait(false);

            // The real IDE connection is live; the side channel has no further purpose.
            await preloadCts.CancelAsync().ConfigureAwait(false);
            await preloadTask.ConfigureAwait(false);

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
                var idePrefix = ideId switch
                {
                    "visualstudio" => "vs",
                    "vscode"       => "vscode",
                    _              => "lsp",
                };
                var logPath = Path.Combine(logDir,
                    $"reqnroll-{idePrefix}-crash-{DateTime.Now:yyyyMMdd-HHmmss}.log");
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
    /// <param name="logLevel">
    /// The <c>--log-level</c> verbosity requested by the client, defaulting to
    /// <see cref="TraceLevel.Warning"/>. Drives both the file-backed <see cref="IDeveroomLogger"/>
    /// and the OmniSharp protocol-logging minimum level, so wire-level debugging and file logging
    /// stay in lockstep unless a caller explicitly asks for more (e.g. <c>--log-level Verbose</c>).
    /// </param>
    internal static void ConfigureServer(LanguageServerOptions options, string? clientIde = null,
        TraceLevel logLevel = TraceLevel.Warning)
    {
        options.ConfigureLogging(logging =>
        {
            logging.SetMinimumLevel(ToLogLevel(logLevel));
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
            .AddReqnrollLspCoreServices(clientIde, logLevel)
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

            // vscode-languageclient v10 (used by VS Code and Rider) does not wire its
            // DidChangeTextDocumentFeature when textDocumentSync is absent from the static
            // capabilities — dynamic client/registerCapability for textDocument/didChange is
            // silently ignored and the client never sends content-change notifications.
            // VS's LSP client handles dynamic-only registration correctly, so this static
            // entry is only needed for non-VS clients.
            // Fine-grained selector filtering (*.feature + *.cs) still comes from OmniSharp's
            // dynamic registration once the feature infrastructure is activated.
            if (!string.Equals(clientIde, "visualstudio", StringComparison.OrdinalIgnoreCase))
            {
                response.Capabilities.TextDocumentSync = new TextDocumentSyncOptions
                {
                    Change = TextDocumentSyncKind.Full,
                    OpenClose = true
                };
            }

            // textDocument/prepareRename and textDocument/rename are registered via OnRequest
            // (manual routing) and therefore do NOT automatically populate server capabilities.
            // Without renameProvider, vscode-languageclient never sets editorHasRenameProvider,
            // F2 is inert, and no rename request reaches the server.
            // VS does not need this: it invokes rename via the custom intercepting-pipe flow
            // (reqnroll/renameTargets → reqnroll/selectRenameTarget) and advertising renameProvider
            // to VS would cause its standard rename UI to appear alongside the custom dialog.
            if (!string.Equals(clientIde, "visualstudio", StringComparison.OrdinalIgnoreCase))
            {
                response.Capabilities.RenameProvider = new RenameRegistrationOptions.StaticOptions
                {
                    PrepareProvider = true
                };
            }

            return Task.CompletedTask;
        });
    }

    /// <summary>
    /// Maps the <see cref="IDeveroomLogger"/> verbosity scale onto
    /// <see cref="Microsoft.Extensions.Logging.LogLevel"/> for the OmniSharp protocol-logging pipeline.
    /// </summary>
    internal static LogLevel ToLogLevel(TraceLevel level) => level switch
    {
        TraceLevel.Off     => LogLevel.None,
        TraceLevel.Error   => LogLevel.Error,
        TraceLevel.Warning => LogLevel.Warning,
        TraceLevel.Info    => LogLevel.Information,
        TraceLevel.Verbose => LogLevel.Trace,
        _                  => LogLevel.Warning
    };

    /// <summary>Returns the value following <paramref name="flag"/> in <paramref name="args"/>, or <see langword="null"/> when absent.</summary>
    internal static string? ParseArg(string[] args, string flag)
        => args
            .SkipWhile(a => !string.Equals(a, flag, StringComparison.OrdinalIgnoreCase))
            .Skip(1)
            .FirstOrDefault();

    /// <summary>Parses <c>--log-level</c> from <paramref name="args"/>, defaulting to <see cref="TraceLevel.Warning"/> when absent or unrecognized.</summary>
    internal static TraceLevel ParseLogLevel(string[] args)
        => Enum.TryParse<TraceLevel>(ParseArg(args, "--log-level"), ignoreCase: true, out var parsedLevel)
            ? parsedLevel
            : TraceLevel.Warning;
}
