using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace Reqnroll.IdeSupport.Common.Diagnostics;

public class AsynchronousFileLogger : IDeveroomLogger, IDisposable
{
    private readonly Channel<LogMessage> _channel;
    private readonly IFileSystemForIDE _fileSystem;
    private readonly CancellationTokenSource _stopTokenSource;

    protected AsynchronousFileLogger(IFileSystemForIDE fileSystem, TraceLevel level, string idePrefix = "vs")
    {
        _fileSystem = fileSystem;
        Level = level;
        // Unbounded so that bursts (e.g. 30+ concurrent spec scenarios) never silently drop messages.
        _channel = Channel.CreateUnbounded<LogMessage>();
        _stopTokenSource = new CancellationTokenSource();
        LogFilePath = GetLogFile(idePrefix);
    }

    public string LogFilePath { get; private set; }
    public TraceLevel Level { get; }

    public virtual void Log(LogMessage message)
    {
        if (message.Level > Level) return;
        _channel.Writer.TryWrite(message);
    }

    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    internal static string GetLogFile(string idePrefix = "vs")
    {
        return Path.Combine(Environment.GetFolderPath(
                Environment.SpecialFolder.LocalApplicationData), "Reqnroll",
#if DEBUG
            $"reqnroll-{idePrefix}-debug-{DateTime.UtcNow:yyyyMMdd}.log");
#else
            $"reqnroll-{idePrefix}-{DateTime.Now:yyyyMMdd}.log");
#endif
    }

    public static AsynchronousFileLogger CreateInstance(IFileSystemForIDE fileSystem, string idePrefix = "vs")
    {
        var fileLogger = new AsynchronousFileLogger(fileSystem, TraceLevel.Verbose, idePrefix);
        Task.Factory.StartNew(
            fileLogger.Start,
            fileLogger._stopTokenSource.Token,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default);
        return fileLogger;
    }

    private Task Start()
    {
        EnsureLogFolder();
        DeleteOldLogFiles();
        return WorkerLoop();
    }

    private async Task WorkerLoop()
    {
        while (!_stopTokenSource.IsCancellationRequested)
            try
            {
                var message = await _channel.Reader.ReadAsync(_stopTokenSource.Token);
                WriteLogMessage(message);
            }
            catch (Exception ex) when (ex is not (ChannelClosedException or TaskCanceledException))
            {
                Debug.WriteLine(ex, $"Error writing to the {LogFilePath}");
            }
            catch
            {
                // ignored
            }
    }

    protected void WriteLogMessage(LogMessage message)
    {
        // Indent continuation lines so multi-line messages (e.g. connector JSON, stack traces)
        // remain visually grouped without losing the structured prefix on the first line.
        var body = message.Message.Replace("\r\n", "\n").Replace("\n", "\n    ");
        var content =
            $"{message.TimeStamp:yyyy-MM-ddTHH\\:mm\\:ss.fffzzz}, {message.Level}@{message.ManagedThreadId}, {message.CallerMethod}: {body}";
        if (message.Exception != null) content += $"\n    : {message.Exception}".Replace("\n", "\n    ");
        content += Environment.NewLine;

        _fileSystem.File.AppendAllText(LogFilePath, content, Encoding.UTF8);
    }

    protected void EnsureLogFolder()
    {
        LogFilePath = Path.GetFullPath(LogFilePath);
        var logFolder = Path.GetDirectoryName(LogFilePath);
        if (!_fileSystem.Directory.Exists(logFolder))
            _fileSystem.Directory.CreateDirectory(logFolder);
    }

    private void DeleteOldLogFiles()
    {
        try
        {
            var logFolder = Path.GetDirectoryName(LogFilePath);
            if (!Directory.Exists(logFolder))
                return;

            var logFiles = Directory.GetFiles(logFolder, "reqnroll-*.log");

            foreach (string logFile in logFiles)
            {
                FileInfo fi = new FileInfo(logFile);
                if (fi.LastWriteTime < DateTime.UtcNow.AddDays(-10))
                    fi.Delete();
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine(ex, "Error deleting log files");
        }
    }

    protected virtual void Dispose(bool disposing)
    {
        if (!disposing) return;

        _channel.Writer.TryComplete();
        _stopTokenSource.Cancel(true);
        _stopTokenSource.Dispose();
    }
}
