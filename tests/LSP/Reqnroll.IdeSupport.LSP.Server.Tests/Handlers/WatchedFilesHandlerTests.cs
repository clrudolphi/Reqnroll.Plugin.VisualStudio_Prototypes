using MediatR;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using Reqnroll.IdeSupport.Common.Diagnostics;
using Reqnroll.IdeSupport.LSP.Server.Handlers.ProtocolHandlers;
using Reqnroll.IdeSupport.LSP.Server.Workspace;

namespace Reqnroll.IdeSupport.LSP.Server.Tests.Handlers;

public class WatchedFilesHandlerTests
{
    private readonly ILspWorkspaceScopeManager _scopeManager = Substitute.For<ILspWorkspaceScopeManager>();
    private readonly IMediator _mediator = Substitute.For<IMediator>();
    private readonly IDeveroomLogger _logger = Substitute.For<IDeveroomLogger>();

    private WatchedFilesHandler CreateSut() =>
        new(_scopeManager, _mediator, _logger);

    private static DidChangeWatchedFilesParams MakeParams(string filePath, FileChangeType changeType)
    {
        var uri = DocumentUri.FromFileSystemPath(filePath);
        return new DidChangeWatchedFilesParams
        {
            Changes = new Container<FileEvent>(new FileEvent
            {
                Uri = uri,
                Type = changeType
            })
        };
    }

    // ── No matching scope ─────────────────────────────────────────────────────

    [Fact]
    public async Task Handle_logs_verbose_and_skips_when_no_scope_matches()
    {
        _scopeManager.GetScopeForUri(Arg.Any<DocumentUri>()).Returns((Reqnroll.IdeSupport.Common.ProjectSystem.IProjectScope?)null);

        var sut = CreateSut();
        var result = await sut.Handle(MakeParams("/unknown/workspace/reqnroll.json", FileChangeType.Changed), CancellationToken.None);

        result.Should().Be(Unit.Value);
        // No scope found — should log verbosely (via extension method) and not publish
        await _mediator.DidNotReceiveWithAnyArgs().Publish(Arg.Any<INotification>(), Arg.Any<CancellationToken>());
    }

    // ── Matching scope ────────────────────────────────────────────────────────

    [Fact]
    public async Task Handle_publishes_notification_when_scope_matches()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(tempDir);

        try
        {
            var ideScope = new LspIdeScope(_logger);
            var projectScope = new LspProjectScope(tempDir, ideScope);
            _scopeManager.GetScopeForUri(Arg.Any<DocumentUri>()).Returns(projectScope);

            var sut = CreateSut();
            var configPath = Path.Combine(tempDir, "reqnroll.json");
            await sut.Handle(MakeParams(configPath, FileChangeType.Changed), CancellationToken.None);

            await _mediator.Received(1).Publish(
                Arg.Any<INotification>(),
                Arg.Any<CancellationToken>());
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public async Task Handle_returns_Unit_on_empty_changes()
    {
        var sut = CreateSut();
        var request = new DidChangeWatchedFilesParams
        {
            Changes = new Container<FileEvent>()
        };
        var result = await sut.Handle(request, CancellationToken.None);
        result.Should().Be(Unit.Value);
    }

    // ── GetRegistrationOptions ────────────────────────────────────────────────

    [Fact]
    public void GetRegistrationOptions_includes_reqnroll_json_watcher()
    {
        var sut = CreateSut();
        var options = sut.GetRegistrationOptions(null!, null!);
        options.Watchers.Should().ContainSingle(w =>
            w.GlobPattern.ToString()!.Contains("reqnroll.json"));
    }
}
