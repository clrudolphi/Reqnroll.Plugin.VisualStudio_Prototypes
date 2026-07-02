using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Reqnroll.IdeSupport.LSP.Server.Hosting;

namespace Reqnroll.IdeSupport.LSP.Server.Tests.Hosting;

public class ProgramTests
{
    [Fact]
    public void ParseLogLevel_defaults_to_Warning_when_flag_is_absent()
    {
        Program.ParseLogLevel(new[] { "--ide", "visualstudio" }).Should().Be(TraceLevel.Warning);
    }

    [Fact]
    public void ParseLogLevel_defaults_to_Warning_for_no_args()
    {
        Program.ParseLogLevel(Array.Empty<string>()).Should().Be(TraceLevel.Warning);
    }

    [Theory]
    [InlineData("Off", TraceLevel.Off)]
    [InlineData("error", TraceLevel.Error)]
    [InlineData("WARNING", TraceLevel.Warning)]
    [InlineData("Info", TraceLevel.Info)]
    [InlineData("verbose", TraceLevel.Verbose)]
    public void ParseLogLevel_parses_case_insensitively(string arg, TraceLevel expected)
    {
        Program.ParseLogLevel(new[] { "--ide", "vscode", "--log-level", arg }).Should().Be(expected);
    }

    [Fact]
    public void ParseLogLevel_defaults_to_Warning_for_an_unrecognized_value()
    {
        Program.ParseLogLevel(new[] { "--log-level", "not-a-level" }).Should().Be(TraceLevel.Warning);
    }

    [Fact]
    public void ParseArg_returns_the_value_following_the_flag()
    {
        Program.ParseArg(new[] { "--ide", "visualstudio", "--log-level", "Verbose" }, "--ide")
            .Should().Be("visualstudio");
    }

    [Fact]
    public void ParseArg_returns_null_when_the_flag_is_absent()
    {
        Program.ParseArg(new[] { "--ide", "vscode" }, "--log-level").Should().BeNull();
    }

    [Theory]
    [InlineData(TraceLevel.Off, LogLevel.None)]
    [InlineData(TraceLevel.Error, LogLevel.Error)]
    [InlineData(TraceLevel.Warning, LogLevel.Warning)]
    [InlineData(TraceLevel.Info, LogLevel.Information)]
    [InlineData(TraceLevel.Verbose, LogLevel.Trace)]
    public void ToLogLevel_maps_each_TraceLevel_to_the_matching_LogLevel(TraceLevel traceLevel, LogLevel expected)
    {
        Program.ToLogLevel(traceLevel).Should().Be(expected);
    }
}
