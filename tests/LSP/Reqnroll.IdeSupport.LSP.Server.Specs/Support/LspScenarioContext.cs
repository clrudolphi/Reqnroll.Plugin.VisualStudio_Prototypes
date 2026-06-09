using OmniSharp.Extensions.LanguageServer.Protocol;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using Reqnroll.IdeSupport.LSP.Server.Protocol;

namespace Reqnroll.IdeSupport.LSP.Server.Specs.Support;

/// <summary>
/// Per-scenario state shared between step classes via Reqnroll's container.
/// Owns the <see cref="LspServerHarness"/> and a temporary workspace folder.
/// </summary>
public sealed class LspScenarioContext
{
    public LspScenarioContext()
    {
        WorkspaceFolder = Path.Combine(Path.GetTempPath(), "ReqnrollLspSpecs", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(WorkspaceFolder);
    }

    public LspServerHarness Harness { get; } = new();
    public string WorkspaceFolder { get; }
    public bool Started { get; set; }

    // Tracking for the most recently opened document, used by Then-steps.
    public DocumentUri? LastUri { get; set; }
    public string LastDocumentText { get; set; } = string.Empty;
    public int LastVersion { get; set; }
    public SemanticTokens? LastTokens { get; set; }
    public LocationOrLocationLinks? LastReferences { get; set; }
    public FindStepUsagesResponse? LastFindStepUsages { get; set; }

    public DocumentUri UriFor(string relativeName)
        => DocumentUri.FromFileSystemPath(Path.Combine(WorkspaceFolder, relativeName));

    public async Task EnsureStartedAsync(string? ideId = null)
    {
        if (Started) return;
        await Harness.StartAsync(WorkspaceFolder, ideId).ConfigureAwait(false);
        Started = true;
    }

    public async Task DisposeAsync()
    {
        await Harness.DisposeAsync().ConfigureAwait(false);
        try { if (Directory.Exists(WorkspaceFolder)) Directory.Delete(WorkspaceFolder, recursive: true); }
        catch { /* best-effort temp cleanup */ }
    }
}
