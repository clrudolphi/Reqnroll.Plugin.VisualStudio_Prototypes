#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using OmniSharp.Extensions.LanguageServer.Protocol;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using LspRange = OmniSharp.Extensions.LanguageServer.Protocol.Models.Range;
using Reqnroll.IdeSupport.Common;
using Reqnroll.IdeSupport.Common.Diagnostics;
using Reqnroll.IdeSupport.LSP.Core.Discovery;
using Reqnroll.IdeSupport.LSP.Core.Matching;
using Reqnroll.IdeSupport.LSP.Core.Rename;
using Reqnroll.IdeSupport.LSP.Server.Discovery;
using Reqnroll.IdeSupport.LSP.Server.Document;
using Reqnroll.IdeSupport.LSP.Server.Protocol;
using Reqnroll.IdeSupport.LSP.Server.Services;
using Reqnroll.IdeSupport.LSP.Server.Workspace;

namespace Reqnroll.IdeSupport.LSP.Server.Handlers.ProtocolHandlers;

/// <summary>
/// Handles <c>textDocument/prepareRename</c>, <c>textDocument/rename</c>,
/// <c>reqnroll/renameTargets</c>, and <c>reqnroll/selectRenameTarget</c> for the
/// F16 Step Rename Refactoring feature.
/// </summary>
public sealed class StepRenameHandler
{
    private readonly IBindingMatchService          _matchService;
    private readonly ILspWorkspaceScopeManager     _scopeManager;
    private readonly IProjectBindingRegistryLookup _registryLookup;
    private readonly IDeveroomLogger               _logger;
    private readonly IDocumentBufferService        _documentBuffer;
    private readonly RenameSessionManager          _sessionManager;

    public StepRenameHandler(
        IBindingMatchService          matchService,
        ILspWorkspaceScopeManager     scopeManager,
        IProjectBindingRegistryLookup registryLookup,
        IDeveroomLogger               logger,
        IDocumentBufferService        documentBuffer)
    {
        _matchService    = matchService;
        _scopeManager    = scopeManager;
        _registryLookup  = registryLookup;
        _logger          = logger;
        _documentBuffer  = documentBuffer;
        _sessionManager  = new RenameSessionManager();
    }

    // ── textDocument/prepareRename ──────────────────────────────────────────────

    /// <summary>
    /// Validates that the cursor is on a renameable binding. Returns the range
    /// of the renameable text (attribute string or step text), or <c>null</c>
    /// if rename is not available at this position.
    /// </summary>
    public Task<LspRange?> HandlePrepareRenameAsync(
        PrepareRenameParams request,
        CancellationToken   cancellationToken)
    {
        var uri  = request.TextDocument.Uri;
        var path = uri.GetFileSystemPath();

        if (string.IsNullOrEmpty(path))
            return Task.FromResult<LspRange?>(null);

        // Rule 1: validate cursor position (file type)
        var posError = StepRenameValidator.ValidateCursorPosition((Uri)uri);
        if (posError != null)
        {
            _logger.LogVerbose($"StepRenameHandler: prepareRename — {posError.Message}");
            return Task.FromResult<LspRange?>(null);
        }

        // Rule 7: validate project state via the registry lookup
        var registry = _registryLookup.GetRegistryForUri(uri);
        bool isInitialized = registry != ProjectBindingRegistry.Invalid;
        bool hasFeatureFiles = false;

        if (isInitialized)
        {
            var project = _scopeManager.GetProjectForUri(uri);
            hasFeatureFiles = project != null &&
                (_scopeManager.GetIndexedFeatureFiles(project).Count > 0
                 || _scopeManager.ResolveOwners(uri).Count > 0);
        }

        var projError = StepRenameValidator.ValidateProjectState(isInitialized, hasFeatureFiles);
        if (projError != null)
        {
            _logger.LogVerbose($"StepRenameHandler: prepareRename — {projError.Message}");
            return Task.FromResult<LspRange?>(null);
        }

        // For .cs files: check if the cursor resolves to a single binding
        if (path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
        {
            var line   = request.Position.Line + 1;
            var column = request.Position.Character + 1;
            var bindingLocation = new SourceLocation(path, line, column);

            if (registry == ProjectBindingRegistry.Invalid)
                return Task.FromResult<LspRange?>(null);

            var binding = FindBindingAtLocation(registry, bindingLocation);
            if (binding == null)
                return Task.FromResult<LspRange?>(null);

            // Rule 2: validate expression is a string literal
            var exprError = StepRenameValidator.ValidateExpressionIsStringLiteral(binding.Expression);
            if (exprError != null)
            {
                _logger.LogVerbose($"StepRenameHandler: prepareRename — {exprError.Message}");
                return Task.FromResult<LspRange?>(null);
            }

            // Return a simple range highlighting the method line
            var methodRange = new LspRange
            {
                Start = new Position(line - 1, 0),
                End   = new Position(line - 1, 200)
            };
            return Task.FromResult<LspRange?>(methodRange);
        }

        // For .feature files: return the step text range (placeholder)
        if (path.EndsWith(".feature", StringComparison.OrdinalIgnoreCase))
        {
            var line = request.Position.Line;
            return Task.FromResult<LspRange?>(new LspRange
            {
                Start = new Position(line, 0),
                End   = new Position(line, 200)
            });
        }

        return Task.FromResult<LspRange?>(null);
    }

    // ── textDocument/rename ────────────────────────────────────────────────────

    /// <summary>
    /// Executes the rename. Validates the new name, resolves all feature step locations,
    /// resolves the C# attribute string range, and returns a WorkspaceEdit covering all files.
    /// </summary>
    public async Task<WorkspaceEdit?> HandleRenameAsync(
        RenameParams       request,
        CancellationToken   cancellationToken)
    {
        var uri  = request.TextDocument.Uri;
        var path = uri.GetFileSystemPath();
        var newName = request.NewName;

        if (string.IsNullOrEmpty(path) || string.IsNullOrEmpty(newName))
            return null;

        _logger.LogVerbose($"StepRenameHandler: rename at {path}, newName='{newName}'");

        // ── 1. Resolve binding ─────────────────────────────────────────────────

        var line   = request.Position.Line + 1;
        var column = request.Position.Character + 1;

        var registry = _registryLookup.GetRegistryForUri(uri);
        if (registry == ProjectBindingRegistry.Invalid)
        {
            _logger.LogVerbose("StepRenameHandler: registry is invalid");
            return null;
        }

        // Check for a pending rename session (set by reqnroll/selectRenameTarget).
        // This handles the multi-attribute case where the cursor is not on a specific
        // attribute string — the picker pre-selected which binding to rename.
        ProjectStepDefinitionBinding? binding = null;
        int? pendingAttributeIndex = null;

        // Use version from request or fallback to 0
        var documentVersion = 0;
        if (_sessionManager.TryConsume(uri.ToString(), documentVersion, out var sessionAttrIndex))
        {
            pendingAttributeIndex = sessionAttrIndex;
            _logger.LogVerbose($"StepRenameHandler: consumed pending session, attributeIndex={sessionAttrIndex}");
        }

        if (pendingAttributeIndex.HasValue)
        {
            // Find the binding by enumerating bindings at the method location
            var bindingsAtLocation = registry.StepDefinitions
                .Where(b => b.Implementation.SourceLocation != null &&
                            string.Equals(b.Implementation.SourceLocation.SourceFile, path, StringComparison.OrdinalIgnoreCase) &&
                            Math.Abs(b.Implementation.SourceLocation.SourceFileLine - line) <= 5)
                .ToList();

            if (pendingAttributeIndex.Value >= 0 && pendingAttributeIndex.Value < bindingsAtLocation.Count)
            {
                binding = bindingsAtLocation[pendingAttributeIndex.Value];
                _logger.LogVerbose($"StepRenameHandler: resolved binding via session: '{binding?.Expression}'");
            }
        }

        // Fall back to position-based resolution (single-binding case)
        binding ??= FindBindingAtLocation(registry, new SourceLocation(path, line, column));
        if (binding == null)
        {
            _logger.LogVerbose("StepRenameHandler: no binding at cursor position");
            return null;
        }

        var bindingLocation = new SourceLocation(path, line, column);

        // ── 2. Validate new name ───────────────────────────────────────────────
        var expression = binding.Expression ?? string.Empty;
        var nameError = StepRenameValidator.ValidateNewName(expression, newName);
        if (nameError != null)
        {
            _logger.LogVerbose($"StepRenameHandler: validation failed — {nameError.Message}");
            return null;
        }

        // ── 3. Resolve feature step locations ──────────────────────────────────
        var owners = _scopeManager.ResolveOwners(uri);
        var projectFilter = owners.Count > 0
            ? owners.Select(p => new ProjectOwner(p.ProjectFullName, p.TargetFrameworkMoniker)).ToArray()
            : null;

        var usages = _matchService.FindUsages(bindingLocation, projectFilter);
        var changes = new Dictionary<DocumentUri, List<TextEdit>>();

        // ── 4. Build .feature file edits ───────────────────────────────────────
        foreach (var usage in usages)
        {
            var featureUri = DocumentUri.Parse(usage.FeatureDocumentId);
            if (!changes.TryGetValue(featureUri, out var list))
            {
                list = new List<TextEdit>();
                changes[featureUri] = list;
            }

            list.Add(new TextEdit
            {
                Range = usage.Range.ToLspRange(),
                NewText = newName
            });
        }

        // ── 5. Build .cs file edit (if cursor was on C#) ──────────────────────
        if (path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
        {
            var csEdit = await BuildCSharpEditAsync(uri, bindingLocation, registry, expression, newName);
            if (csEdit != null)
            {
                if (!changes.TryGetValue(uri, out var list))
                {
                    list = new List<TextEdit>();
                    changes[uri] = list;
                }
                list.Add(csEdit);
            }
        }

        if (changes.Count == 0)
            return null;

        return new WorkspaceEdit
        {
            Changes = changes.ToDictionary(kvp => kvp.Key, kvp => (IEnumerable<TextEdit>)kvp.Value)
        };
    }

    // ── Custom request handlers ─────────────────────────────────────────────────

    /// <summary>
    /// Handles <c>reqnroll/renameTargets</c> — enumerates all binding attributes
    /// at the cursor position for the multi-attribute picker flow.
    /// </summary>
    public Task<RenameTargetsResponse?> HandleRenameTargetsAsync(
        TextDocumentPositionParams request,
        CancellationToken          cancellationToken)
    {
        var uri  = request.TextDocument.Uri;
        var path = uri.GetFileSystemPath();

        if (string.IsNullOrEmpty(path) || !path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
            return Task.FromResult<RenameTargetsResponse?>(null);

        var line   = request.Position.Line + 1;
        var column = request.Position.Character + 1;
        var bindingLocation = new SourceLocation(path, line, column);

        var registry = _registryLookup.GetRegistryForUri(uri);
        if (registry == ProjectBindingRegistry.Invalid)
            return Task.FromResult<RenameTargetsResponse?>(new RenameTargetsResponse());

        // Collect all bindings at this method location (heuristic: within 5 lines)
        var allBindings = registry.StepDefinitions
            .Where(b => b.Implementation.SourceLocation != null &&
                        string.Equals(b.Implementation.SourceLocation.SourceFile, path, StringComparison.OrdinalIgnoreCase) &&
                        Math.Abs(b.Implementation.SourceLocation.SourceFileLine - line) <= 5)
            .DistinctBy(b => b.Expression)
            .ToList();

        if (allBindings.Count == 0)
            return Task.FromResult<RenameTargetsResponse?>(new RenameTargetsResponse());

        var response = new RenameTargetsResponse();
        int idx = 0;
        foreach (var b in allBindings)
        {
            response.Targets.Add(new RenameTargetItem
            {
                Label = $"{b.StepDefinitionType} {b.Expression ?? "(unknown)"}",
                AttributeIndex = idx,
                StartLine = (b.Implementation.SourceLocation?.SourceFileLine ?? line) - 1,
                StartChar = 1,
                EndLine   = (b.Implementation.SourceLocation?.SourceFileLine ?? line) - 1,
                EndChar   = 200
            });
            idx++;
        }

        return Task.FromResult<RenameTargetsResponse?>(response);
    }

    /// <summary>
    /// Handles <c>reqnroll/selectRenameTarget</c> — stores the selected attribute
    /// for the next <c>textDocument/rename</c> call.
    /// </summary>
    public Task HandleSelectRenameTargetAsync(
        SelectRenameTargetParams request,
        CancellationToken        cancellationToken)
    {
        _sessionManager.SetSession(request.Uri, request.Version, request.AttributeIndex);
        return Task.CompletedTask;
    }

    // ── Helpers ────────────────────────────────────────────────────────────────

    private static ProjectStepDefinitionBinding? FindBindingAtLocation(
        ProjectBindingRegistry registry, SourceLocation location)
    {
        return registry.StepDefinitions
            .FirstOrDefault(b => b.Implementation.SourceLocation != null &&
                                 string.Equals(b.Implementation.SourceLocation.SourceFile, location.SourceFile, StringComparison.OrdinalIgnoreCase) &&
                                 b.Implementation.SourceLocation.SourceFileLine == location.SourceFileLine &&
                                 b.Implementation.SourceLocation.SourceFileColumn == location.SourceFileColumn);
    }

    private async Task<TextEdit?> BuildCSharpEditAsync(
        DocumentUri uri,
        SourceLocation bindingLocation,
        ProjectBindingRegistry registry,
        string originalExpression,
        string newName)
    {
        var csPath = uri.GetFileSystemPath();
        if (string.IsNullOrEmpty(csPath))
            return null;

        // Get file text from the document buffer, or read from disk
        string? fileText = null;
        if (_documentBuffer.TryGet(uri, out var buffer) && buffer?.Text != null)
            fileText = buffer.Text;
        else if (System.IO.File.Exists(csPath))
            fileText = await System.IO.File.ReadAllTextAsync(csPath);

        if (fileText == null)
            return null;

        // Parse into a SyntaxTree and wrap in CSharpStepDefinitionFile
        var tree = Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree.ParseText(fileText);
        var fileDetails = FileDetails.FromPath(csPath);
        var csFile = new CSharpStepDefinitionFile(fileDetails, tree);

        // Find the attribute index by matching the expression
        var allStepDefs = registry.StepDefinitions
            .Where(b => b.Implementation.SourceLocation != null &&
                        string.Equals(b.Implementation.SourceLocation.SourceFile, csPath, StringComparison.OrdinalIgnoreCase) &&
                        b.Implementation.SourceLocation.SourceFileLine == bindingLocation.SourceFileLine)
            .Select((b, i) => new { b.Expression, Index = i })
            .ToList();

        var attrIndex = allStepDefs
            .Where(s => s.Expression == originalExpression)
            .Select(s => s.Index)
            .FirstOrDefault();

        // Parse the .cs to find the attribute string range
        var parser = new StepDefinitionFileParser();
        var info = await parser.GetAttributeStringInfo(
            csFile, bindingLocation.SourceFileLine, bindingLocation.SourceFileColumn, attrIndex, originalExpression);

        if (info == null)
            return null;

        // Convert the character-offset TextSpan to line/column using the SyntaxTree
        var lineSpan = tree.GetLineSpan(info.Span);
        var startPos = lineSpan.StartLinePosition;
        var endPos   = lineSpan.EndLinePosition;

        return new TextEdit
        {
            Range = new LspRange
            {
                Start = new Position(startPos.Line, startPos.Character),
                End   = new Position(endPos.Line, endPos.Character)
            },
            NewText = newName
        };
    }
}
