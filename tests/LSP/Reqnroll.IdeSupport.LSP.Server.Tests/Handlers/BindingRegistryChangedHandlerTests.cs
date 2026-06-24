using MediatR;
using OmniSharp.Extensions.LanguageServer.Protocol.Server;
using Reqnroll.IdeSupport.Common.Diagnostics;
using Reqnroll.IdeSupport.LSP.Server;
using Reqnroll.IdeSupport.LSP.Server.Discovery;
using Reqnroll.IdeSupport.LSP.Server.Handlers.InternalHandlers;
using Reqnroll.IdeSupport.LSP.Server.Notifications;
using Reqnroll.IdeSupport.LSP.Server.Protocol;
using Reqnroll.IdeSupport.LSP.Server.Features.TextSync;
using Reqnroll.IdeSupport.LSP.Server.Services;
using Reqnroll.IdeSupport.LSP.Server.Tests.Discovery;
using Reqnroll.IdeSupport.LSP.Server.Workspace;

namespace Reqnroll.IdeSupport.LSP.Server.Tests.Handlers;

/// <summary>
/// Tests for <see cref="BindingRegistryChangedHandler"/>.
/// Verifies that closed-file scanning uses the membership index (I1) when a baseline has
/// been received and falls back to folder-glob otherwise, and that open-file reparsing
/// uses index ownership rather than folder-prefix when a baseline exists.
/// </summary>
public class BindingRegistryChangedHandlerTests : IDisposable
{
    private readonly IDocumentBufferService       _bufferService = Substitute.For<IDocumentBufferService>();
    private readonly IGherkinDocumentTaggerService _taggerService = Substitute.For<IGherkinDocumentTaggerService>();
    private readonly ILspWorkspaceScopeManager    _scopeManager  = Substitute.For<ILspWorkspaceScopeManager>();
    private readonly ILanguageServerFacade        _languageServer = Substitute.For<ILanguageServerFacade>();
    private readonly ClientIdeContext             _clientIde     = new("visualstudio");
    private readonly IMediator                    _mediator      = Substitute.For<IMediator>();
    private readonly ICSharpBindingDiscoveryService _csharpDiscovery = Substitute.For<ICSharpBindingDiscoveryService>();
    private readonly IDeveroomLogger              _logger        = Substitute.For<IDeveroomLogger>();

    private readonly IDeveroomLogger _ideLogger = Substitute.For<IDeveroomLogger>();
    private readonly LspIdeScope     _ideScope;

    // Two on-disk roots — project folder and linked/external folder.
    private readonly string _projectFolder;
    private readonly string _externalFolder;
    private readonly LspReqnrollProject _project;

    public BindingRegistryChangedHandlerTests()
    {
        _ideScope      = new LspIdeScope(_ideLogger);
        _projectFolder = Path.Combine(Path.GetTempPath(), "BRCHTests_" + Guid.NewGuid().ToString("N"));
        _externalFolder = Path.Combine(Path.GetTempPath(), "BRCHTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_projectFolder);
        Directory.CreateDirectory(_externalFolder);

        _project = DiscoveryTestSupport.MakeProject(_ideScope, _projectFolder);

        // Default: all buffers are empty (no open files).
        _bufferService.All.Returns(Enumerable.Empty<DocumentBuffer>());

        // ScanClosedFileAsync must return a completed Task (NSubstitute default for Task
        // void is already CompletedTask, but be explicit here for clarity).
        _taggerService.ScanClosedFileAsync(
                Arg.Any<DocumentUri>(), Arg.Any<string>(), Arg.Any<LspReqnrollProject>())
            .Returns(Task.CompletedTask);
        // ParseAsync: NSubstitute returns Task.FromResult(null) by default — that is fine
        // since the handler discards the return value.
    }

    public void Dispose()
    {
        _project.Dispose();
        // LspIdeScope is not IDisposable; no cleanup needed.
        try { if (Directory.Exists(_projectFolder))  Directory.Delete(_projectFolder,  recursive: true); } catch { }
        try { if (Directory.Exists(_externalFolder)) Directory.Delete(_externalFolder, recursive: true); } catch { }
    }

    private BindingRegistryChangedHandler CreateSut()
        => CreateSut(_clientIde);

    private BindingRegistryChangedHandler CreateSut(ClientIdeContext clientIde)
        => new(_bufferService, _taggerService, _scopeManager, _languageServer, clientIde, _mediator, _csharpDiscovery, _logger);

    // ── Closed-file scanning — index-driven (baseline received) ───────────────

    [Fact]
    public async Task ScanAllFeatureFiles_uses_indexed_files_when_baseline_received()
    {
        var f1 = Path.Combine(_projectFolder,  "A.feature");
        var f2 = Path.Combine(_externalFolder, "Linked.feature");
        File.WriteAllText(f1, "Feature: A\n");
        File.WriteAllText(f2, "Feature: Linked\n");

        _scopeManager.HasBaselineForProject(_project).Returns(true);
        _scopeManager.GetIndexedFeatureFiles(_project).Returns(new[] { f1, f2 });

        await CreateSut().Handle(
            new BindingRegistryChangedNotification(_project, IsFullReplacement: true),
            CancellationToken.None);

        await _taggerService.Received(1).ScanClosedFileAsync(
            Arg.Is<DocumentUri>(u => FilePathMatches(u, f1)), Arg.Any<string>(), Arg.Any<LspReqnrollProject>());
        await _taggerService.Received(1).ScanClosedFileAsync(
            Arg.Is<DocumentUri>(u => FilePathMatches(u, f2)), Arg.Any<string>(), Arg.Any<LspReqnrollProject>());
    }

    [Fact]
    public async Task ScanAllFeatureFiles_includes_linked_feature_outside_project_folder()
    {
        // Only a linked file — inside _externalFolder, outside _projectFolder.
        var linked = Path.Combine(_externalFolder, "External.feature");
        File.WriteAllText(linked, "Feature: External\n");

        _scopeManager.HasBaselineForProject(_project).Returns(true);
        _scopeManager.GetIndexedFeatureFiles(_project).Returns(new[] { linked });

        await CreateSut().Handle(
            new BindingRegistryChangedNotification(_project, IsFullReplacement: true),
            CancellationToken.None);

        await _taggerService.Received(1).ScanClosedFileAsync(
            Arg.Is<DocumentUri>(u => FilePathMatches(u, linked)), Arg.Any<string>(), Arg.Any<LspReqnrollProject>());
    }

    [Fact]
    public async Task ScanAllFeatureFiles_excludes_project_folder_feature_not_in_index()
    {
        // Index contains only f1; f2 is in the project folder but NOT in the index.
        var f1 = Path.Combine(_projectFolder, "Included.feature");
        var f2 = Path.Combine(_projectFolder, "Excluded.feature");
        File.WriteAllText(f1, "Feature: Included\n");
        File.WriteAllText(f2, "Feature: Excluded\n");

        _scopeManager.HasBaselineForProject(_project).Returns(true);
        _scopeManager.GetIndexedFeatureFiles(_project).Returns(new[] { f1 }); // f2 absent

        await CreateSut().Handle(
            new BindingRegistryChangedNotification(_project, IsFullReplacement: true),
            CancellationToken.None);

        await _taggerService.Received(1).ScanClosedFileAsync(
            Arg.Is<DocumentUri>(u => FilePathMatches(u, f1)), Arg.Any<string>(), Arg.Any<LspReqnrollProject>());
        await _taggerService.DidNotReceive().ScanClosedFileAsync(
            Arg.Is<DocumentUri>(u => FilePathMatches(u, f2)), Arg.Any<string>(), Arg.Any<LspReqnrollProject>());
    }

    // ── Closed-file scanning — folder-glob fallback (no baseline) ────────────

    [Fact]
    public async Task ScanAllFeatureFiles_falls_back_to_folder_glob_when_no_baseline()
    {
        var featureFile = Path.Combine(_projectFolder, "Glob.feature");
        File.WriteAllText(featureFile, "Feature: Glob\n");

        _scopeManager.HasBaselineForProject(_project).Returns(false);

        await CreateSut().Handle(
            new BindingRegistryChangedNotification(_project, IsFullReplacement: true),
            CancellationToken.None);

        await _taggerService.Received(1).ScanClosedFileAsync(
            Arg.Is<DocumentUri>(u => FilePathMatches(u, featureFile)), Arg.Any<string>(), Arg.Any<LspReqnrollProject>());
    }

    [Fact]
    public async Task ScanAllFeatureFiles_glob_fallback_returns_early_when_folder_does_not_exist()
    {
        var project = DiscoveryTestSupport.MakeProject(
            _ideScope,
            Path.Combine(Path.GetTempPath(), "does_not_exist_" + Guid.NewGuid().ToString("N")));
        _scopeManager.HasBaselineForProject(project).Returns(false);

        await CreateSut().Handle(
            new BindingRegistryChangedNotification(project, IsFullReplacement: true),
            CancellationToken.None);

        await _taggerService.DidNotReceive().ScanClosedFileAsync(
            Arg.Any<DocumentUri>(), Arg.Any<string>(), Arg.Any<LspReqnrollProject>());

        project.Dispose();
    }

    // ── Open-file skip during closed-file scan ────────────────────────────────

    [Fact]
    public async Task ScanAllFeatureFiles_skips_already_open_feature_files()
    {
        var featureFile = Path.Combine(_projectFolder, "Open.feature");
        File.WriteAllText(featureFile, "Feature: Open\n");

        var openUri = DocumentUri.FromFileSystemPath(featureFile);
        _bufferService.All.Returns(new[] { new DocumentBuffer(openUri, 1, "Feature: Open\n") });

        _scopeManager.HasBaselineForProject(_project).Returns(true);
        _scopeManager.GetIndexedFeatureFiles(_project).Returns(new[] { featureFile });

        await CreateSut().Handle(
            new BindingRegistryChangedNotification(_project, IsFullReplacement: true),
            CancellationToken.None);

        // Already open → ScanClosedFileAsync must NOT be called.
        await _taggerService.DidNotReceive().ScanClosedFileAsync(
            Arg.Any<DocumentUri>(), Arg.Any<string>(), Arg.Any<LspReqnrollProject>());
    }

    // ── Open-file reparsing ───────────────────────────────────────────────────

    [Fact]
    public async Task ReparseOpenFiles_uses_index_ownership_when_baseline_received()
    {
        var ownedUri   = DocumentUri.FromFileSystemPath(Path.Combine(_projectFolder, "Owned.feature"));
        var foreignUri = DocumentUri.FromFileSystemPath(Path.Combine(_projectFolder, "Foreign.feature"));

        _bufferService.All.Returns(new[]
        {
            new DocumentBuffer(ownedUri,   1, "Feature: Owned\n"),
            new DocumentBuffer(foreignUri, 1, "Feature: Foreign\n")
        });

        _scopeManager.HasBaselineForProject(_project).Returns(true);
        _scopeManager.GetProjectsForUri(ownedUri).Returns(new[] { _project });
        _scopeManager.GetProjectsForUri(foreignUri).Returns(Array.Empty<LspReqnrollProject>());

        await CreateSut().Handle(
            new BindingRegistryChangedNotification(_project, IsFullReplacement: false),
            CancellationToken.None);

        await _taggerService.Received(1).ParseAsync(ownedUri,   Arg.Any<int?>());
        await _taggerService.DidNotReceive().ParseAsync(foreignUri, Arg.Any<int?>());
    }

    [Fact]
    public async Task ReparseOpenFiles_uses_folder_prefix_when_no_baseline()
    {
        var inFolderUri  = DocumentUri.FromFileSystemPath(Path.Combine(_projectFolder,  "Inside.feature"));
        var outsideUri   = DocumentUri.FromFileSystemPath(Path.Combine(_externalFolder, "Outside.feature"));

        _bufferService.All.Returns(new[]
        {
            new DocumentBuffer(inFolderUri, 1, "Feature: Inside\n"),
            new DocumentBuffer(outsideUri,  1, "Feature: Outside\n")
        });

        _scopeManager.HasBaselineForProject(_project).Returns(false);

        await CreateSut().Handle(
            new BindingRegistryChangedNotification(_project, IsFullReplacement: false),
            CancellationToken.None);

        await _taggerService.Received(1).ParseAsync(inFolderUri, Arg.Any<int?>());
        await _taggerService.DidNotReceive().ParseAsync(outsideUri, Arg.Any<int?>());
    }

    // ── IsFullReplacement = false does not trigger closed-file scan ───────────

    [Fact]
    public async Task Handle_incremental_does_not_trigger_ScanAllFeatureFiles()
    {
        // IsFullReplacement = false → only open files are reparsed, no closed-file scan.
        _scopeManager.HasBaselineForProject(_project).Returns(true);
        _scopeManager.GetIndexedFeatureFiles(_project).Returns(Array.Empty<string>());

        await CreateSut().Handle(
            new BindingRegistryChangedNotification(_project, IsFullReplacement: false),
            CancellationToken.None);

        await _taggerService.DidNotReceive().ScanClosedFileAsync(
            Arg.Any<DocumentUri>(), Arg.Any<string>(), Arg.Any<LspReqnrollProject>());
        _scopeManager.DidNotReceive().GetIndexedFeatureFiles(Arg.Any<LspReqnrollProject>());
    }

    // ── workspace/codeLens/refresh — correct client guard ─────────────────────

    [Fact]
    public async Task Handle_fullReplacement_sends_codeLens_refresh_for_non_vs_client()
    {
        var nonVsIde = new ClientIdeContext("vscode");
        var sut = new BindingRegistryChangedHandler(
            _bufferService, _taggerService, _scopeManager, _languageServer, nonVsIde, _mediator, _csharpDiscovery, _logger);

        _scopeManager.HasBaselineForProject(_project).Returns(true);
        _scopeManager.GetIndexedFeatureFiles(_project).Returns(Array.Empty<string>());

        await sut.Handle(
            new BindingRegistryChangedNotification(_project, IsFullReplacement: true),
            CancellationToken.None);

        _languageServer.Client.Received(1).SendRequest("workspace/codeLens/refresh");
    }

    [Fact]
    public async Task Handle_fullReplacement_does_not_send_codeLens_refresh_for_vs_client()
    {
        // _clientIde is constructed with "visualstudio" in the test fixture
        _scopeManager.HasBaselineForProject(_project).Returns(true);
        _scopeManager.GetIndexedFeatureFiles(_project).Returns(Array.Empty<string>());

        await CreateSut().Handle(
            new BindingRegistryChangedNotification(_project, IsFullReplacement: true),
            CancellationToken.None);

        _languageServer.Client.DidNotReceive().SendRequest("workspace/codeLens/refresh");
    }

    [Fact]
    public async Task Handle_incremental_does_not_send_codeLens_refresh_even_for_non_vs_client()
    {
        var nonVsIde = new ClientIdeContext("vscode");
        var sut = new BindingRegistryChangedHandler(
            _bufferService, _taggerService, _scopeManager, _languageServer, nonVsIde, _mediator, _csharpDiscovery, _logger);

        _scopeManager.HasBaselineForProject(_project).Returns(true);
        _scopeManager.GetIndexedFeatureFiles(_project).Returns(Array.Empty<string>());

        await sut.Handle(
            new BindingRegistryChangedNotification(_project, IsFullReplacement: false),
            CancellationToken.None);

        _languageServer.Client.DidNotReceive().SendRequest("workspace/codeLens/refresh");
    }

    // ── .cs rediscovery after full replacement (stale-DLL reconciliation) ─────

    [Fact]
    public async Task Rediscover_reconciles_closed_cs_file_edited_since_build()
    {
        // Assembly built an hour ago; Steps.cs saved just now (edited but not rebuilt) — the
        // exact "edit, save, restart VS" scenario where the compiled binding is stale.
        var buildTime = DateTime.UtcNow.AddHours(-1);
        var project   = MakeProjectWithBuiltAssembly(buildTime);

        var stepsPath = WriteCsFile("Steps.cs", "// renamed step", DateTime.UtcNow);

        IndexBindingFiles(project, stepsPath);

        await CreateSut().Handle(
            new BindingRegistryChangedNotification(project, IsFullReplacement: true),
            CancellationToken.None);

        await _csharpDiscovery.Received(1).UpdateFromSourceForProjectAsync(
            project,
            Arg.Is<string>(p => PathEq(p, stepsPath)),
            Arg.Is<string>(t => t.Contains("renamed step")),
            Arg.Any<CancellationToken>());

        project.Dispose();
    }

    [Fact]
    public async Task Rediscover_skips_closed_cs_file_unchanged_since_build()
    {
        // Steps.cs is older than the assembly → the DLL faithfully represents it → skip.
        var buildTime = DateTime.UtcNow.AddHours(-1);
        var project   = MakeProjectWithBuiltAssembly(buildTime);

        var stepsPath = WriteCsFile("Steps.cs", "// in sync with DLL", DateTime.UtcNow.AddHours(-2));

        IndexBindingFiles(project, stepsPath);

        await CreateSut().Handle(
            new BindingRegistryChangedNotification(project, IsFullReplacement: true),
            CancellationToken.None);

        await _csharpDiscovery.DidNotReceive().UpdateFromSourceForProjectAsync(
            Arg.Any<LspReqnrollProject>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());

        project.Dispose();
    }

    [Fact]
    public async Task Rediscover_reconciles_open_dirty_cs_buffer_regardless_of_timestamp()
    {
        // Disk copy is older than the build, but the open buffer has unsaved edits that the DLL
        // can never reflect → must reconcile using the buffer text, not the disk text.
        var buildTime = DateTime.UtcNow.AddHours(-1);
        var project   = MakeProjectWithBuiltAssembly(buildTime);

        var openPath = WriteCsFile("OpenSteps.cs", "// stale disk text", DateTime.UtcNow.AddHours(-2));
        var openUri  = DocumentUri.FromFileSystemPath(openPath);
        _bufferService.All.Returns(new[] { new DocumentBuffer(openUri, 3, "// unsaved buffer edit") });

        IndexBindingFiles(project, openPath);

        await CreateSut().Handle(
            new BindingRegistryChangedNotification(project, IsFullReplacement: true),
            CancellationToken.None);

        // Reconciled exactly once, with the BUFFER text — not the on-disk text.
        await _csharpDiscovery.Received(1).UpdateFromSourceForProjectAsync(
            project,
            Arg.Is<string>(p => PathEq(p, openPath)),
            Arg.Is<string>(t => t.Contains("unsaved buffer edit")),
            Arg.Any<CancellationToken>());
        await _csharpDiscovery.DidNotReceive().UpdateFromSourceForProjectAsync(
            project, Arg.Any<string>(), Arg.Is<string>(t => t.Contains("stale disk text")), Arg.Any<CancellationToken>());

        project.Dispose();
    }

    [Fact]
    public async Task Rediscover_skips_closed_files_when_project_not_built()
    {
        // No output assembly exists → nothing compiled can be stale → closed files are not read.
        // (_project's default OutputAssemblyPath points at a file that was never created.)
        var stepsPath = WriteCsFile("Steps.cs", "// newer than nothing", DateTime.UtcNow);

        IndexBindingFiles(_project, stepsPath);

        await CreateSut().Handle(
            new BindingRegistryChangedNotification(_project, IsFullReplacement: true),
            CancellationToken.None);

        await _csharpDiscovery.DidNotReceive().UpdateFromSourceForProjectAsync(
            Arg.Any<LspReqnrollProject>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Rediscover_does_not_run_on_incremental_change()
    {
        var buildTime = DateTime.UtcNow.AddHours(-1);
        var project   = MakeProjectWithBuiltAssembly(buildTime);
        var stepsPath = WriteCsFile("Steps.cs", "// edited", DateTime.UtcNow);
        IndexBindingFiles(project, stepsPath);

        // IsFullReplacement = false → no stale-DLL reconciliation (it's a live Roslyn patch path).
        await CreateSut().Handle(
            new BindingRegistryChangedNotification(project, IsFullReplacement: false),
            CancellationToken.None);

        await _csharpDiscovery.DidNotReceive().UpdateFromSourceForProjectAsync(
            Arg.Any<LspReqnrollProject>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());

        project.Dispose();
    }

    // ── Code-lens refresh signal after full replacement ──────────────────────

    [Fact]
    public async Task FullReplacement_pushes_reqnroll_refreshCodeLens_for_visual_studio()
    {
        _scopeManager.HasBaselineForProject(_project).Returns(true);
        _scopeManager.GetIndexedFeatureFiles(_project).Returns(Array.Empty<string>());

        await CreateSut().Handle(
            new BindingRegistryChangedNotification(_project, IsFullReplacement: true),
            CancellationToken.None);

        _languageServer.Received(1).SendNotification(
            "reqnroll/refreshCodeLens",
            Arg.Is<RefreshCodeLensParams>(p => p.ProjectName == _project.ProjectName));
    }

    [Fact]
    public async Task Incremental_change_does_not_push_refreshCodeLens()
    {
        _scopeManager.HasBaselineForProject(_project).Returns(true);

        await CreateSut().Handle(
            new BindingRegistryChangedNotification(_project, IsFullReplacement: false),
            CancellationToken.None);

        _languageServer.DidNotReceive().SendNotification(
            "reqnroll/refreshCodeLens", Arg.Any<RefreshCodeLensParams>());
    }

    [Fact]
    public async Task FullReplacement_does_not_push_refreshCodeLens_for_non_visual_studio()
    {
        _scopeManager.HasBaselineForProject(_project).Returns(true);
        _scopeManager.GetIndexedFeatureFiles(_project).Returns(Array.Empty<string>());

        // VS Code / Rider use the standard workspace/codeLens/refresh request instead.
        await CreateSut(new ClientIdeContext("vscode")).Handle(
            new BindingRegistryChangedNotification(_project, IsFullReplacement: true),
            CancellationToken.None);

        _languageServer.DidNotReceive().SendNotification(
            "reqnroll/refreshCodeLens", Arg.Any<RefreshCodeLensParams>());
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    /// <summary>Creates a project whose output assembly exists on disk with the given write time.</summary>
    private LspReqnrollProject MakeProjectWithBuiltAssembly(DateTime assemblyWriteUtc)
    {
        var assemblyPath = Path.Combine(_projectFolder, "bin", "Debug", "App.dll");
        Directory.CreateDirectory(Path.GetDirectoryName(assemblyPath)!);
        File.WriteAllText(assemblyPath, "fake-dll");
        File.SetLastWriteTimeUtc(assemblyPath, assemblyWriteUtc);
        return DiscoveryTestSupport.MakeProject(_ideScope, _projectFolder, outputAssemblyPath: assemblyPath);
    }

    /// <summary>Writes a .cs file under the project folder with a controlled last-write time.</summary>
    private string WriteCsFile(string name, string content, DateTime writeUtc)
    {
        var path = Path.Combine(_projectFolder, name);
        File.WriteAllText(path, content);
        File.SetLastWriteTimeUtc(path, writeUtc);
        return path;
    }

    /// <summary>Marks the project as baselined and attributes the given .cs files to it in the index.</summary>
    private void IndexBindingFiles(LspReqnrollProject project, params string[] bindingFiles)
    {
        _scopeManager.HasBaselineForProject(project).Returns(true);
        _scopeManager.GetBindingFilePathsForProject(project).Returns(bindingFiles);
        // Keep the (separate) feature-file scan a no-op for these tests.
        _scopeManager.GetIndexedFeatureFiles(project).Returns(Array.Empty<string>());
    }

    private static bool PathEq(string actual, string expected)
        => string.Equals(Path.GetFullPath(actual), Path.GetFullPath(expected), StringComparison.OrdinalIgnoreCase);

    private static bool FilePathMatches(DocumentUri uri, string expected)
    {
        var actual = uri.GetFileSystemPath();
        return string.Equals(
            actual  is null ? null : Path.GetFullPath(actual),
            Path.GetFullPath(expected),
            StringComparison.OrdinalIgnoreCase);
    }
}
