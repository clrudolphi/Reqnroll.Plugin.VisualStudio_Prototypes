namespace Reqnroll.IdeSupport.LSP.Server.Protocol;

/// <summary>
/// Centralizes all LSP method names (both standard and custom Reqnroll extensions)
/// used by the language server. This prevents magic strings scattered across the codebase
/// and makes refactoring or auditing registered endpoints much easier.
/// </summary>
public static class LspMethodNames
{
    // ── Custom Reqnroll Extensions ───────────────────────────────────────────
    public const string ReqnrollProjectLoaded = "reqnroll/projectLoaded";
    public const string ReqnrollProjectUnloaded = "reqnroll/projectUnloaded";
    public const string ReqnrollProjectFiles = "reqnroll/projectFiles";
    public const string ReqnrollFindStepUsages = "reqnroll/findStepUsages";
    public const string ReqnrollGoToStepDefinitions = "reqnroll/goToStepDefinitions";
    public const string ReqnrollGoToHooks = "reqnroll/goToHooks";
    public const string ReqnrollFindUnusedStepDefinitions = "reqnroll/findUnusedStepDefinitions";
    public const string ReqnrollRenameTargets = "reqnroll/renameTargets";
    public const string ReqnrollSelectRenameTarget = "reqnroll/selectRenameTarget";
    public const string ReqnrollRefreshCodeLens = "reqnroll/refreshCodeLens";
    public const string ReqnrollSemanticTokens = "reqnroll/semanticTokens";

    // ── Standard LSP Methods ────────────────────────────────────────────────
    public const string TextDocumentSemanticTokensFull = "textDocument/semanticTokens/full";
    public const string TextDocumentSemanticTokensFullDelta = "textDocument/semanticTokens/full/delta";
    public const string TextDocumentReferences = "textDocument/references";
    public const string TextDocumentCodeLens = "textDocument/codeLens";
    public const string TextDocumentPrepareRename = "textDocument/prepareRename";
    public const string TextDocumentRename = "textDocument/rename";
    public const string TextDocumentPublishDiagnostics = "textDocument/publishDiagnostics";

    // ── Workspace Methods ───────────────────────────────────────────────────
    public const string WorkspaceApplyEdit = "workspace/applyEdit";
    public const string WorkspaceCodeLensRefresh = "workspace/codeLens/refresh";

    // ── Telemetry ───────────────────────────────────────────────────────────
    public const string TelemetryEvent = "telemetry/event";
}
