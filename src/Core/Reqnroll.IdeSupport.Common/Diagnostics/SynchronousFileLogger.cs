using System;
using System.Diagnostics;

namespace Reqnroll.IdeSupport.Common.Diagnostics;

public class SynchronousFileLogger : AsynchronousFileLogger
{
    public SynchronousFileLogger(string idePrefix = "vs")
        : base(new FileSystemForIDE(), TraceLevel.Verbose, idePrefix)
    {
        EnsureLogFolder();
    }

    public override void Log(LogMessage message)
    {
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
