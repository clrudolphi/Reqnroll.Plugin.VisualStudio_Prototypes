using System;
using System.Diagnostics;

namespace Reqnroll.IdeSupport.Common.Diagnostics;

public class SynchronousFileLogger : AsynchronousFileLogger
{
    public SynchronousFileLogger(string ide = "vs", string role = "ext", TraceLevel level = TraceLevel.Warning)
        : base(new FileSystemForIDE(), level, ide, role)
    {
        EnsureLogFolder();
    }

    public override void Log(LogMessage message)
    {
        if (message.Level > Level) return;

        try
        {
            WriteLogMessage(message);
        }
        catch (Exception ex)
        {
            Debug.WriteLine(ex, $"Error writing to the {LogFilePath}");
        }
    }
}
