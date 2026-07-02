using System.Diagnostics;
using Reqnroll.IdeSupport.LSP.Server.Hosting;

namespace Reqnroll.IdeSupport.LSP.Server.Tests.Hosting;

public class ClientIdeContextTests
{
    [Fact]
    public void Default_log_level_is_Warning()
    {
        new ClientIdeContext("visualstudio").LogLevel.Should().Be(TraceLevel.Warning);
    }

    [Theory]
    [InlineData(TraceLevel.Off)]
    [InlineData(TraceLevel.Error)]
    [InlineData(TraceLevel.Warning)]
    [InlineData(TraceLevel.Info)]
    [InlineData(TraceLevel.Verbose)]
    public void Explicit_log_level_is_honored(TraceLevel level)
    {
        new ClientIdeContext("vscode", level).LogLevel.Should().Be(level);
    }

    [Fact]
    public void Ide_and_IsVisualStudio_are_unaffected_by_log_level()
    {
        var context = new ClientIdeContext("visualstudio", TraceLevel.Verbose);

        context.Ide.Should().Be("visualstudio");
        context.IsVisualStudio.Should().BeTrue();
    }
}
