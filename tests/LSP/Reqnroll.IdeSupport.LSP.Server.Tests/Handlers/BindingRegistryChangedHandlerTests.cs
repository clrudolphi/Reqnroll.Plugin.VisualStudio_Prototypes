using MediatR;
using Reqnroll.IdeSupport.Common.Diagnostics;
using Reqnroll.IdeSupport.LSP.Server.Handlers.InternalHandlers;
using Reqnroll.IdeSupport.LSP.Server.Notifications;
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
    private readonly IMediator                    _mediator      = Substitute.For<IMediator>();
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
        => new(_bufferService, _taggerService, _scopeManager, _mediator, _logger);

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

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static bool FilePathMatches(DocumentUri uri, string expected)
    {
        var actual = uri.GetFileSystemPath();
        return string.Equals(
            actual  is null ? null : Path.GetFullPath(actual),
            Path.GetFullPath(expected),
            StringComparison.OrdinalIgnoreCase);
    }
}
