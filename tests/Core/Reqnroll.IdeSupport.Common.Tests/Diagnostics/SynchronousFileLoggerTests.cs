using System.IO;

namespace Reqnroll.IdeSupport.Common.Tests.Diagnostics;

public class SynchronousFileLoggerTests
{
    [Fact]
    public void Default_level_is_Warning()
    {
        var logger = new SynchronousFileLogger("test", $"default-{Guid.NewGuid():N}");
        try
        {
            logger.Level.Should().Be(TraceLevel.Warning);
        }
        finally
        {
            DeleteLogFile(logger);
        }
    }

    [Theory]
    [InlineData(TraceLevel.Off)]
    [InlineData(TraceLevel.Error)]
    [InlineData(TraceLevel.Warning)]
    [InlineData(TraceLevel.Info)]
    [InlineData(TraceLevel.Verbose)]
    public void Explicit_level_is_honored(TraceLevel level)
    {
        var logger = new SynchronousFileLogger("test", $"explicit-{Guid.NewGuid():N}", level);
        try
        {
            logger.Level.Should().Be(level);
        }
        finally
        {
            DeleteLogFile(logger);
        }
    }

    [Fact]
    public void Messages_above_the_configured_level_are_dropped()
    {
        var logger = new SynchronousFileLogger("test", $"filter-{Guid.NewGuid():N}", TraceLevel.Warning);
        try
        {
            logger.Log(new LogMessage(TraceLevel.Info, "should be dropped",
                nameof(Messages_above_the_configured_level_are_dropped)));

            File.Exists(logger.LogFilePath).Should().BeFalse(
                "Info is below the Warning threshold and should never be written");
        }
        finally
        {
            DeleteLogFile(logger);
        }
    }

    [Fact]
    public void Messages_at_or_below_the_configured_level_are_written()
    {
        var logger = new SynchronousFileLogger("test", $"filter-{Guid.NewGuid():N}", TraceLevel.Warning);
        try
        {
            logger.Log(new LogMessage(TraceLevel.Warning, "should be written",
                nameof(Messages_at_or_below_the_configured_level_are_written)));

            File.ReadAllText(logger.LogFilePath).Should().Contain("should be written");
        }
        finally
        {
            DeleteLogFile(logger);
        }
    }

    private static void DeleteLogFile(SynchronousFileLogger logger)
    {
        try { File.Delete(logger.LogFilePath); } catch { /* best-effort cleanup */ }
    }
}
