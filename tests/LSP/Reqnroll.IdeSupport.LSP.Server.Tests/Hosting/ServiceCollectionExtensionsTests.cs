using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Reqnroll.IdeSupport.LSP.Server.Hosting;

namespace Reqnroll.IdeSupport.LSP.Server.Tests.Hosting;

public class ServiceCollectionExtensionsTests
{
    [Fact]
    public void AddReqnrollLspCoreServices_registers_the_requested_log_level_on_ClientIdeContext()
    {
        var provider = new ServiceCollection()
            .AddReqnrollLspCoreServices("vscode", TraceLevel.Verbose)
            .BuildServiceProvider();

        var context = provider.GetRequiredService<ClientIdeContext>();

        context.Ide.Should().Be("vscode");
        context.LogLevel.Should().Be(TraceLevel.Verbose);
    }

    [Fact]
    public void AddReqnrollLspCoreServices_defaults_the_log_level_to_Warning()
    {
        var provider = new ServiceCollection()
            .AddReqnrollLspCoreServices("visualstudio")
            .BuildServiceProvider();

        provider.GetRequiredService<ClientIdeContext>().LogLevel.Should().Be(TraceLevel.Warning);
    }
}
