using MediatR;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using Reqnroll.IdeSupport.Common.Diagnostics;
using Reqnroll.IdeSupport.LSP.Server.Features.TextSync;
using Reqnroll.IdeSupport.LSP.Server.Handlers.ProtocolHandlers;
using Reqnroll.IdeSupport.LSP.Server.Workspace;

namespace Reqnroll.IdeSupport.LSP.Server.Tests.Handlers;

public class WorkspaceFoldersHandlerTests
{
    private readonly ILspWorkspaceScopeManager _scopeManager = Substitute.For<ILspWorkspaceScopeManager>();
    private readonly IDeveroomLogger _logger = Substitute.For<IDeveroomLogger>();

    private WorkspaceFoldersHandler CreateSut() => new(_scopeManager, _logger);

    private static WorkspaceFolder FolderFrom(string path) =>
        new() { Uri = DocumentUri.FromFileSystemPath(path), Name = System.IO.Path.GetFileName(path) };

    private static DidChangeWorkspaceFoldersParams MakeParams(
        IEnumerable<WorkspaceFolder>? added = null,
        IEnumerable<WorkspaceFolder>? removed = null)
        => new()
        {
            Event = new WorkspaceFoldersChangeEvent
            {
                Added = new Container<WorkspaceFolder>(added ?? []),
                Removed = new Container<WorkspaceFolder>(removed ?? [])
            }
        };

    // ── Added folders ─────────────────────────────────────────────────────────

    [Fact]
    public async Task Handle_calls_OpenWorkspace_for_each_added_folder()
    {
        var path1 = "/workspace/project1";
        var path2 = "/workspace/project2";

        var sut = CreateSut();
        await sut.Handle(MakeParams(added: [FolderFrom(path1), FolderFrom(path2)]), CancellationToken.None);

        _scopeManager.Received(1).OpenWorkspace(Arg.Is<string>(s => s.EndsWith("project1")));
        _scopeManager.Received(1).OpenWorkspace(Arg.Is<string>(s => s.EndsWith("project2")));
    }

    // ── Removed folders ───────────────────────────────────────────────────────

    [Fact]
    public async Task Handle_calls_CloseWorkspace_for_each_removed_folder()
    {
        var path = "/workspace/project1";

        var sut = CreateSut();
        await sut.Handle(MakeParams(removed: [FolderFrom(path)]), CancellationToken.None);

        _scopeManager.Received(1).CloseWorkspace(Arg.Is<string>(s => s.EndsWith("project1")));
    }

    // ── Mixed ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Handle_processes_added_and_removed_independently()
    {
        var added = "/workspace/new";
        var removed = "/workspace/old";

        var sut = CreateSut();
        await sut.Handle(MakeParams(added: [FolderFrom(added)], removed: [FolderFrom(removed)]), CancellationToken.None);

        _scopeManager.Received(1).OpenWorkspace(Arg.Any<string>());
        _scopeManager.Received(1).CloseWorkspace(Arg.Any<string>());
    }

    // ── Returns Unit ─────────────────────────────────────────────────────────

    [Fact]
    public async Task Handle_returns_Unit()
    {
        var sut = CreateSut();
        var result = await sut.Handle(MakeParams(), CancellationToken.None);
        result.Should().Be(Unit.Value);
    }

    // ── GetRegistrationOptions ────────────────────────────────────────────────

    [Fact]
    public void GetRegistrationOptions_enables_change_notifications()
    {
        var sut = CreateSut();
        var options = sut.GetRegistrationOptions(null!);
        options.ChangeNotifications.Should().NotBeNull();
    }
}
