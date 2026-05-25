using Reqnroll.IdeSupport.Common.Diagnostics;
using System.Diagnostics;

namespace Reqnroll.IdeSupport.LSP.Server.Diagnostics;

/// <summary>
/// <see cref="IDeveroomLogger"/> used by the LSP server process.
/// Delegates to a <see cref="DeveroomCompositeLogger"/> composed of:
/// <list type="bullet">
///   <item><see cref="DeveroomDebugLogger"/> — writes to <see cref="Debug"/> output</item>
///   <item><see cref="SynchronousFileLogger"/> — appends to the Reqnroll log file</item>
/// </list>
/// </summary>
public sealed class LspDeveroomLogger : IDeveroomLogger
{
    private readonly DeveroomCompositeLogger _inner;

    public LspDeveroomLogger()
    {
        _inner = new DeveroomCompositeLogger()
            .Add(new DeveroomDebugLogger())
            .Add(new SynchronousFileLogger());
    }

    public TraceLevel Level => _inner.Level;

    public void Log(LogMessage message) => _inner.Log(message);
}
