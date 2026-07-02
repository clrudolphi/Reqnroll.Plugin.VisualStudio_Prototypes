using System;
using System.Diagnostics;

namespace Reqnroll.IdeSupport.LSP.Server.Hosting;

/// <summary>
/// Carries the <c>--ide</c> identifier of the connecting client so that handlers can vary
/// behaviour per IDE.  Registered as a singleton in <see cref="Program.ConfigureServer"/>.
/// </summary>
public sealed class ClientIdeContext
{
    public ClientIdeContext(string? ide, TraceLevel logLevel = TraceLevel.Warning)
    {
        Ide = ide;
        LogLevel = logLevel;
    }

    /// <summary>The raw <c>--ide</c> value, or <see langword="null"/> when absent.</summary>
    public string? Ide { get; }

    /// <summary>
    /// The file/protocol log verbosity requested via <c>--log-level</c>, defaulting to
    /// <see cref="TraceLevel.Warning"/> when the client did not specify one.
    /// </summary>
    public TraceLevel LogLevel { get; }

    /// <summary>
    /// True when the connecting client is Visual Studio, whose built-in LSP semantic-token
    /// colorizer cannot map custom token types — so the server pushes tokens to it instead of
    /// relying on it to pull them. See <see cref="Handlers.InternalHandlers.SemanticTokensPushHandler"/>.
    /// </summary>
    public bool IsVisualStudio => string.Equals(Ide, "visualstudio", StringComparison.OrdinalIgnoreCase);
}
