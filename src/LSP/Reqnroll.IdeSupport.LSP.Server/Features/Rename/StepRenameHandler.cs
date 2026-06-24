#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using OmniSharp.Extensions.LanguageServer.Protocol;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using LspRange = OmniSharp.Extensions.LanguageServer.Protocol.Models.Range;
using Reqnroll.IdeSupport.Common;
using Reqnroll.IdeSupport.Common.Diagnostics;
using Reqnroll.IdeSupport.LSP.Core.Bindings;

using Reqnroll.IdeSupport.LSP.Core.Discovery;

using Reqnroll.IdeSupport.LSP.Core.Gherkin.Parsing;
using Reqnroll.IdeSupport.LSP.Core.Matching;
using Reqnroll.IdeSupport.LSP.Core.Rename;
using Reqnroll.IdeSupport.LSP.Server.Discovery;
using Reqnroll.IdeSupport.LSP.Server.Document;
using Reqnroll.IdeSupport.LSP.Server.Protocol;
using Reqnroll.IdeSupport.LSP.Server.Features.TextSync;
using Reqnroll.IdeSupport.LSP.Server.Services;
using Reqnroll.IdeSupport.LSP.Server.Workspace;

namespace Reqnroll.IdeSupport.LSP.Server.Features.Rename;

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
    private readonly ILspTelemetryService?         _telemetryService;

    public StepRenameHandler(
        IBindingMatchService          matchService,
        ILspWorkspaceScopeManager     scopeManager,
        IProjectBindingRegistryLookup registryLookup,
        IDeveroomLogger               logger,
        IDocumentBufferService        documentBuffer,
        ILspTelemetryService?         telemetryService = null)
    {
        _matchService    = matchService;
        _scopeManager    = scopeManager;
        _registryLookup  = registryLookup;
        _logger          = logger;
        _documentBuffer  = documentBuffer;
        _sessionManager  = new RenameSessionManager();
        _telemetryService = telemetryService;
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

            var binding = registry.FindBindingAtLocation(bindingLocation);
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
        List<ProjectStepDefinitionBinding> bindingsAtLocation = new();

        // Use version from request or fallback to 0
        var documentVersion = 0;
        if (_sessionManager.TryConsume(uri.ToString(), documentVersion, out var sessionAttrIndex))
        {
            pendingAttributeIndex = sessionAttrIndex;
            _logger.LogVerbose($"StepRenameHandler: consumed pending session, attributeIndex={sessionAttrIndex}");
        }

        if (pendingAttributeIndex.HasValue)
        {
            if (path.EndsWith(".feature", StringComparison.OrdinalIgnoreCase))
            {
                // For feature files, resolve bindings from the match cache
                bindingsAtLocation = FindBindingsAtFeatureStep(uri, path, position: request.Position);
            }
            else
            {
                // For C# files, find bindings at the method location in the registry
                bindingsAtLocation = registry.StepDefinitions
                    .Where(b => b.Implementation.SourceLocation != null &&
                                string.Equals(b.Implementation.SourceLocation.SourceFile, path, StringComparison.OrdinalIgnoreCase) &&
                                Math.Abs(b.Implementation.SourceLocation.SourceFileLine - line) <= 5)
                    .ToList();
            }

            if (pendingAttributeIndex.Value >= 0 && pendingAttributeIndex.Value < bindingsAtLocation.Count)
            {
                binding = bindingsAtLocation[pendingAttributeIndex.Value];
                _logger.LogVerbose($"StepRenameHandler: resolved binding via session: '{binding?.Expression}'");
            }
        }

        // Fall back to position-based resolution (single-binding case)
        if (binding == null && path.EndsWith(".feature", StringComparison.OrdinalIgnoreCase))
        {
            var featureBindings = FindBindingsAtFeatureStep(uri, path, position: request.Position);
            binding = featureBindings.FirstOrDefault();
            if (binding != null)
                _logger.LogVerbose($"StepRenameHandler: resolved binding via feature match cache: '{binding.Expression}'");
        }
        binding ??= registry.FindBindingAtLocation(new SourceLocation(path, line, column));
        if (binding == null)
        {
            _logger.LogVerbose("StepRenameHandler: no binding at cursor position");
            return null;
        }

        SourceLocation bindingLocation;

        // Use the binding's C# source location for FindUsages so we can find
        // feature steps that reference this binding. For .feature-originated
        // renames, the request path is the .feature file, not the .cs file.
        if (!path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) &&
            binding.Implementation?.SourceLocation?.SourceFile != null)
        {
            bindingLocation = new SourceLocation(
                binding.Implementation.SourceLocation.SourceFile,
                binding.Implementation.SourceLocation.SourceFileLine,
                binding.Implementation.SourceLocation.SourceFileColumn);
            _logger.LogVerbose($"StepRenameHandler: using binding source location for FindUsages: {bindingLocation}");
        }
        else
        {
            bindingLocation = new SourceLocation(path, line, column);
        }

        // ── 2. Validate new name ───────────────────────────────────────────────
        var expression = binding.Expression ?? string.Empty;
        var nameError = StepRenameValidator.ValidateNewName(expression, newName);
        if (nameError != null)
        {
            _logger.LogVerbose($"StepRenameHandler: validation failed — {nameError.Message}");
            return null;
        }

        // Resolve the live source expression once (preserves the original parameter syntax).
        // For a .cs-invoked rename this is the attribute string literal; otherwise it falls back
        // to the registry expression. It anchors both the feature edits (static-segment
        // substitution) and the C# attribute edit.
        var sourceLiteral = await FindAttributeLiteralAsync(uri, binding);
        var sourceExpression = sourceLiteral?.Token.ValueText ?? expression;

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

            // Read the feature step text to preserve parameter values / placeholders
            string? stepText = null;
            if (usage.Range != null)
            {
                var stepRange = usage.Range.ToLspRange();
                stepText = ReadStepText(featureUri, stepRange);
            }

            var featureNewText = FeatureStepTextBuilder.Build(newName, sourceExpression, binding.Regex, stepText);
            list.Add(new TextEdit
            {
                Range = usage.Range!.ToLspRange(),
                NewText = featureNewText
            });
        }

        // ── 5. Build .cs file edit ────────────────────────────────────────────
        if (sourceLiteral != null)
        {
            var csEdit = BuildCSharpEdit(sourceLiteral, newName);
            if (csEdit != null)
            {
                var csUri = path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)
                    ? uri
                    : DocumentUri.FromFileSystemPath(binding.Implementation!.SourceLocation!.SourceFile);
                if (!changes.TryGetValue(csUri, out var list))
                {
                    list = new List<TextEdit>();
                    changes[csUri] = list;
                }
                list.Add(csEdit);
            }
        }

        if (changes.Count == 0)
            return null;

        // Invalidate the match cache for feature files that were modified by the rename.
        // When a feature file is closed at rename time, no didChange notification fires,
        // so the server's in-memory match cache would otherwise retain the old step text
        // until the file is re-opened and re-parsed.
        foreach (var changedUri in changes.Keys)
        {
            var changedPath = changedUri.GetFileSystemPath();
            if (!string.IsNullOrEmpty(changedPath) && changedPath.EndsWith(".feature", StringComparison.OrdinalIgnoreCase))
            {
                _matchService.InvalidateAllForDocument(changedUri.ToString());
                _logger.LogVerbose($"StepRenameHandler: invalidated match cache for '{changedUri}'");
            }
        }

        // Telemetry
        _telemetryService?.SendEvent("Rename step command executed", new()
        {
            ["Erroneous"] = false,
        });

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
    public async Task<RenameTargetsResponse?> HandleRenameTargetsAsync(
        TextDocumentPositionParams request,
        CancellationToken          cancellationToken)
    {
        var uri  = request.TextDocument.Uri;
        var path = uri.GetFileSystemPath();

        if (string.IsNullOrEmpty(path))
            return null;

        if (path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
        {
            return await HandleRenameTargetsFromCSharpAsync(uri, path, request.Position, cancellationToken);
        }

        if (path.EndsWith(".feature", StringComparison.OrdinalIgnoreCase))
        {
            return await HandleRenameTargetsFromFeatureAsync(uri, path, request.Position, cancellationToken);
        }

        return null;
    }

    private async Task<RenameTargetsResponse?> HandleRenameTargetsFromCSharpAsync(
        DocumentUri uri, string path, Position position, CancellationToken cancellationToken)
    {
        var line = position.Line + 1;

        var registry = _registryLookup.GetRegistryForUri(uri);
        if (registry == ProjectBindingRegistry.Invalid)
            return new RenameTargetsResponse();

        // Collect all bindings at this method location (heuristic: within 5 lines)
        var allBindings = registry.StepDefinitions
            .Where(b => b.Implementation.SourceLocation != null &&
                        string.Equals(b.Implementation.SourceLocation.SourceFile, path, StringComparison.OrdinalIgnoreCase) &&
                        Math.Abs(b.Implementation.SourceLocation.SourceFileLine - line) <= 5)
            .ToList();

        if (allBindings.Count == 0)
            return new RenameTargetsResponse();

        var response = new RenameTargetsResponse();
        int idx = 0;
        foreach (var b in allBindings)
        {
            // Prefer the live source expression (preserves Cucumber parameter types)
            var sourceLiteral = await FindAttributeLiteralAsync(uri, b);
            var expression = sourceLiteral?.Token.ValueText ?? b.Expression ?? "(unknown)";

            var scopeTag = b.Scope?.Tag?.ToString();
            var scopeSuffix = !string.IsNullOrEmpty(scopeTag) ? $" [@{scopeTag}]" : "";
            response.Targets.Add(new RenameTargetItem
            {
                Label = $"{b.StepDefinitionType} {expression}{scopeSuffix}",
                Expression = expression,
                AttributeIndex = idx,
                StartLine = (b.Implementation.SourceLocation?.SourceFileLine ?? line) - 1,
                StartChar = 1,
                EndLine   = (b.Implementation.SourceLocation?.SourceFileLine ?? line) - 1,
                EndChar   = 200
            });
            idx++;
        }

        return response;
    }

    private async Task<RenameTargetsResponse?> HandleRenameTargetsFromFeatureAsync(
        DocumentUri uri, string path, Position position, CancellationToken cancellationToken)
    {
        var matchedBindings = FindBindingsAtFeatureStep(uri, path, position: position);
        if (matchedBindings.Count == 0)
            return new RenameTargetsResponse();

        var response = new RenameTargetsResponse();
        int idx = 0;
        foreach (var b in matchedBindings)
        {
            response.Targets.Add(new RenameTargetItem
            {
                Label = $"{b.StepDefinitionType} {b.Expression ?? "(unknown)"}",
                Expression = b.Expression ?? "",
                AttributeIndex = idx,
                StartLine = 0, StartChar = 0, EndLine = 0, EndChar = 200
            });
            idx++;
        }

        return response;
    }

    /// <summary>
    /// Finds all bindings that match the feature step at the given cursor position
    /// by querying the binding match cache for the owning projects.
    /// </summary>
    private List<ProjectStepDefinitionBinding> FindBindingsAtFeatureStep(
        DocumentUri uri, string path, Position position)
    {
        var uriStr = uri.ToString();
        var owners = _scopeManager.ResolveOwners(uri);
        if (owners.Count == 0)
            return new List<ProjectStepDefinitionBinding>();

        var matchedBindings = new HashSet<ProjectStepDefinitionBinding>();

        foreach (var owner in owners)
        {
            var key = new MatchSetKey(uriStr, new ProjectOwner(owner.ProjectFullName, owner.TargetFrameworkMoniker));
            if (!_matchService.TryGet(key, out var matchSet))
                continue;

            foreach (var step in matchSet.Steps)
            {
                if (step.Result is null || !step.Result.HasDefined)
                    continue;

                // Check if cursor falls within the step's range
                var startPos = step.Range.StartLinePosition;
                var endPos   = step.Range.EndLinePosition;
                if (position.Line >= startPos.Line &&
                    position.Line <= endPos.Line)
                {
                    var stepStartChar = (position.Line == startPos.Line) ? startPos.Character : 0;
                    var stepEndChar   = (position.Line == endPos.Line)   ? endPos.Character   : int.MaxValue;
                    if (position.Character >= stepStartChar && position.Character <= stepEndChar)
                    {
                        foreach (var item in step.Result.Items)
                        {
                            if (item.MatchedStepDefinition != null)
                                matchedBindings.Add(item.MatchedStepDefinition);
                        }
                    }
                }
            }
        }

        return matchedBindings.ToList();
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

    private TextEdit? BuildCSharpEdit(
        LiteralExpressionSyntax? literalArgument,
        string newName)
    {
        if (literalArgument == null)
        {
            _logger.LogVerbose("StepRenameHandler: BuildCSharpEdit — no attribute literal found");
            return null;
        }

        // Preserve the parameter tokens as written in the source. The rename dialog edits
        // the non-parameter text only; the parameter slots must keep their original syntax
        // (e.g. a Cucumber '{int}' stays '{int}', a regex '(.*)' stays '(.*)') rather than
        // whatever projection the dialog happened to seed.
        var sourceExpression = literalArgument.Token.ValueText;
        var finalText = ReconcileParameterTokens(sourceExpression, newName);

        // Convert the character-offset TextSpan to line/column using the SyntaxTree
        var lineSpan = literalArgument.SyntaxTree!.GetLineSpan(literalArgument.Token.Span);
        var startPos = lineSpan.StartLinePosition;
        var endPos   = lineSpan.EndLinePosition;

        _logger.LogVerbose($"StepRenameHandler: BuildCSharpEdit — returning edit at ({startPos.Line},{startPos.Character})-({endPos.Line},{endPos.Character}): '{finalText}'");

        return new TextEdit
        {
            Range = new LspRange
            {
                Start = new Position(startPos.Line, startPos.Character),
                End   = new Position(endPos.Line, endPos.Character)
            },
            NewText = "\"" + finalText + "\""
        };
    }

    /// <summary>
    /// Resolves the string-literal attribute argument for <paramref name="binding"/> by its
    /// SOURCE LOCATION, not by matching the registry's expression text. The registry
    /// expression is a discovery-time projection (a Cucumber expression is rendered to a regex
    /// during discovery, and it reflects the last compiled build rather than the live buffer),
    /// so it cannot be relied on to equal the raw attribute string literal. Line drift from a
    /// stale build is tolerated by choosing the nearest candidate method.
    /// </summary>
    private async Task<LiteralExpressionSyntax?> FindAttributeLiteralAsync(
        DocumentUri uri,
        ProjectStepDefinitionBinding binding)
    {
        var csPath = uri.GetFileSystemPath();
        if (string.IsNullOrEmpty(csPath))
        {
            if (binding?.Implementation?.SourceLocation?.SourceFile != null)
            {
                csPath = binding.Implementation.SourceLocation.SourceFile;
                _logger.LogVerbose($"StepRenameHandler: FindAttributeLiteralAsync — using binding source file '{csPath}'");
            }
            else
            {
                _logger.LogVerbose("StepRenameHandler: FindAttributeLiteralAsync — csPath is null/empty");
                return null;
            }
        }
        else if (!csPath.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
        {
            // When called from a .feature file, use the binding's C# source file
            if (binding?.Implementation?.SourceLocation?.SourceFile != null)
            {
                csPath = binding.Implementation.SourceLocation.SourceFile;
                _logger.LogVerbose($"StepRenameHandler: FindAttributeLiteralAsync — redirected from '{uri.GetFileSystemPath()}' to binding source '{csPath}'");
            }
            else
            {
                _logger.LogVerbose($"StepRenameHandler: FindAttributeLiteralAsync — non-cs file and no binding source: '{csPath}'");
                return null;
            }
        }

        // Get file text from the document buffer, or read from disk
        string? fileText = null;
        var csUri = string.Equals(uri.GetFileSystemPath(), csPath, StringComparison.OrdinalIgnoreCase)
            ? uri
            : DocumentUri.FromFileSystemPath(csPath);
        if (_documentBuffer.TryGet(csUri, out var buffer) && buffer?.Text != null)
        {
            fileText = buffer.Text;
            _logger.LogVerbose($"StepRenameHandler: FindAttributeLiteralAsync — got text from buffer ({fileText.Length} chars)");
        }
        else if (System.IO.File.Exists(csPath))
        {
            fileText = await System.IO.File.ReadAllTextAsync(csPath);
            _logger.LogVerbose($"StepRenameHandler: FindAttributeLiteralAsync — got text from disk ({fileText.Length} chars)");
        }

        if (fileText == null)
        {
            _logger.LogVerbose("StepRenameHandler: FindAttributeLiteralAsync — no file text available");
            return null;
        }

        var tree = Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree.ParseText(fileText);
        var rootNode = await tree.GetRootAsync();

        var stepType = binding.StepDefinitionType;
        var candidates = rootNode
            .DescendantNodes()
            .OfType<MethodDeclarationSyntax>()
            .Select(m => (Method: m,
                          Line: tree.GetLineSpan(m.Identifier.Span).StartLinePosition.Line + 1)) // 1-based
            .Where(x => GetStepAttributeLiterals(x.Method, stepType).Any())
            .ToList();

        if (candidates.Count == 0)
            return null;

        var targetLine = binding.Implementation?.SourceLocation?.SourceFileLine;
        var chosen = targetLine.HasValue
            ? candidates.OrderBy(x => Math.Abs(x.Line - targetLine.Value)).ThenBy(x => x.Line).First()
            : candidates.First();

        // Among the chosen method's matching step attributes, pick the literal to rewrite.
        // A single matching attribute (the common case) is selected regardless of its text.
        // When a method carries several same-type attributes, prefer the one whose literal
        // equals the registry expression, falling back to the first.
        var literals = GetStepAttributeLiterals(chosen.Method, stepType).ToList();
        return literals.FirstOrDefault(e => e.Token.ValueText == binding.Expression)
               ?? literals[0];
    }

    /// <summary>
    /// Rebuilds <paramref name="newExpression"/> so that its parameter slots carry the exact
    /// tokens from <paramref name="sourceExpression"/> (positionally). This keeps the original
    /// parameter syntax — a Cucumber <c>{int}</c> stays <c>{int}</c>, a regex <c>(.*)</c> stays
    /// <c>(.*)</c> — even when the rename dialog seeded a different projection. The user's edits
    /// to the non-parameter text are preserved. When the slot counts differ, the user's text is
    /// honoured verbatim.
    /// </summary>
    internal static string ReconcileParameterTokens(string sourceExpression, string newExpression)
    {
        var originalSlots = StepExpressionParameters.ExtractSlots(sourceExpression);
        if (originalSlots.Count == 0)
            return newExpression;

        var newSlots = StepExpressionParameters.ExtractSlots(newExpression);
        if (newSlots.Count != originalSlots.Count)
            return newExpression;

        var sb = new System.Text.StringBuilder();
        var slotIndex = 0;
        var i = 0;
        while (i < newExpression.Length)
        {
            var slotLength = StepExpressionParameters.SlotLengthAt(newExpression, i);
            if (slotLength > 0)
            {
                sb.Append(originalSlots[slotIndex]);
                slotIndex++;
                i += slotLength;
            }
            else
            {
                sb.Append(newExpression[i]);
                i++;
            }
        }

        return sb.ToString();
    }

    /// <summary>
    /// Returns the first string-literal argument of every attribute on <paramref name="method"/>
    /// that is a step-definition attribute for <paramref name="stepType"/> (<c>Given</c>/<c>When</c>/
    /// <c>Then</c>, or <c>StepDefinition</c> which applies to all step kinds).
    /// </summary>
    private static IEnumerable<LiteralExpressionSyntax> GetStepAttributeLiterals(
        MethodDeclarationSyntax method, ScenarioBlock stepType)
    {
        foreach (var attr in method.AttributeLists.SelectMany(al => al.Attributes))
        {
            if (!IsStepAttributeFor(attr, stepType))
                continue;

            var literal = attr.ArgumentList?.Arguments
                .Select(a => a.Expression)
                .OfType<LiteralExpressionSyntax>()
                .FirstOrDefault(e => e.RawKind == (int)SyntaxKind.StringLiteralExpression);

            if (literal != null)
                yield return literal;
        }
    }

    private static bool IsStepAttributeFor(AttributeSyntax attr, ScenarioBlock stepType)
    {
        var name = attr.Name switch
        {
            QualifiedNameSyntax q => q.Right.Identifier.Text,
            SimpleNameSyntax    s => s.Identifier.Text,
            _                     => attr.Name.ToString()
        };

        if (name.EndsWith("Attribute", StringComparison.Ordinal))
            name = name.Substring(0, name.Length - "Attribute".Length);

        // [StepDefinition("…")] registers for Given/When/Then alike.
        if (string.Equals(name, "StepDefinition", StringComparison.Ordinal))
            return true;

        return stepType switch
        {
            ScenarioBlock.Given => name == "Given",
            ScenarioBlock.When  => name == "When",
            ScenarioBlock.Then  => name == "Then",
            _                   => name is "Given" or "When" or "Then"
        };
    }

    // ── Feature step text parameter preservation ─────────────────────────────

    /// <summary>
    /// Reads the step text from a feature file at the given range, using the
    /// document buffer if available (open file) or reading from disk.
    /// </summary>
    private string? ReadStepText(DocumentUri featureUri, LspRange range)
    {
        string? fileText = null;
        if (_documentBuffer.TryGet(featureUri, out var buffer) && buffer?.Text != null)
            fileText = buffer.Text;
        else
        {
            var path = featureUri.GetFileSystemPath();
            if (!string.IsNullOrEmpty(path) && System.IO.File.Exists(path))
                fileText = System.IO.File.ReadAllText(path);
        }

        if (fileText == null)
            return null;

        var lines = fileText.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None);
        if (range.Start.Line < 0 || range.Start.Line >= lines.Length)
            return null;

        var line = lines[range.Start.Line];
        var start = Math.Min(range.Start.Character, line.Length);
        var end   = Math.Min(range.End.Character, line.Length);
        return start < end ? line.Substring(start, end - start) : null;
    }
}
