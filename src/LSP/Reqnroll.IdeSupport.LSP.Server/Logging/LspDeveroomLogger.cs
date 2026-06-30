using System.Reflection;
using Reqnroll.IdeSupport.Common.Diagnostics;
using Reqnroll.IdeSupport.LSP.Server.Hosting;

namespace Reqnroll.IdeSupport.LSP.Server.Logging;

/// <summary>
/// <see cref="IDeveroomLogger"/> used by the LSP server process.
/// Delegates to a <see cref="DeveroomCompositeLogger"/> composed of:
/// <list type="bullet">
///   <item><see cref="DeveroomDebugLogger"/> — writes to <see cref="Debug"/> output</item>
///   <item><see cref="SynchronousFileLogger"/> — appends to the Reqnroll log file</item>
/// </list>
/// Emits a session-start banner as the first log line so runs within a day-appended file
/// can be distinguished by version, PID, and server path.
/// </summary>
public sealed class LspDeveroomLogger : IDeveroomLogger
{
    private readonly DeveroomCompositeLogger _inner;

    public LspDeveroomLogger(ClientIdeContext clientIdeContext)
    {
        var idePrefix = clientIdeContext.Ide switch
        {
            "visualstudio" => "vs",
            "vscode"       => "vscode",
            _              => "lsp"   // unknown or absent --ide; avoid misattributing to a known IDE
        };
        _inner = new DeveroomCompositeLogger()
            .Add(new DeveroomDebugLogger())
            .Add(new SynchronousFileLogger(idePrefix));

        var version   = Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "unknown";
        var location  = Assembly.GetExecutingAssembly().Location;
        var pid       = Environment.ProcessId;
        this.LogInfo($"=== Reqnroll LSP Server started — v{version}, PID {pid}, {location} ===");
    }

    public TraceLevel Level => _inner.Level;

    public void Log(LogMessage message) => _inner.Log(message);
}
