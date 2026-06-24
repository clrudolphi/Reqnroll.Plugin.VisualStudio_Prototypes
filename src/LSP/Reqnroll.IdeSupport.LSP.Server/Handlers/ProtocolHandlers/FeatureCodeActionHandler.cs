#nullable enable

using System.IO;
using OmniSharp.Extensions.LanguageServer.Protocol;
using OmniSharp.Extensions.LanguageServer.Protocol.Client.Capabilities;
using OmniSharp.Extensions.LanguageServer.Protocol.Document;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using Reqnroll.IdeSupport.Common.Configuration;
using Reqnroll.IdeSupport.Common.Diagnostics;
using Reqnroll.IdeSupport.Common.ProjectSystem.Configuration;
using Reqnroll.IdeSupport.LSP.Core.Editor.Scaffolding;
using Reqnroll.IdeSupport.LSP.Core.Matching;
using Reqnroll.IdeSupport.LSP.Server.Workspace;
using LspRange = OmniSharp.Extensions.LanguageServer.Protocol.Models.Range;

namespace Reqnroll.IdeSupport.LSP.Server.Handlers.ProtocolHandlers;

/// <summary>
/// Handles <c>textDocument/codeAction</c> requests for <c>*.feature</c> files (F6 — Define Steps).
/// Returns code actions that generate C# step-definition stubs for undefined steps.
/// Registered via OmniSharp dynamic registration (<see cref="ICodeActionHandler"/>), scoped to
/// <c>**/*.feature</c> documents so it does not conflict with the C# language server.
/// </summary>
public sealed class FeatureCodeActionHandler : ICodeActionHandler
{
    private readonly IBindingMatchService          _matchService;
    private readonly IStepScaffoldService          _scaffoldService;
    private readonly ILspWorkspaceScopeManager     _scopeManager;
    private readonly IDeveroomLogger               _logger;

    public FeatureCodeActionHandler(
        IBindingMatchService      matchService,
        IStepScaffoldService      scaffoldService,
        ILspWorkspaceScopeManager scopeManager,
        IDeveroomLogger            logger)
    {
        _matchService    = matchService;
        _scaffoldService = scaffoldService;
        _scopeManager    = scopeManager;
        _logger          = logger;
    }

    public CodeActionRegistrationOptions GetRegistrationOptions(
        CodeActionCapability capability,
        ClientCapabilities   clientCapabilities)
        => new()
        {
            DocumentSelector = new TextDocumentSelector(
                new TextDocumentFilter { Pattern = "**/*.feature" }),
            CodeActionKinds = new Container<CodeActionKind>(CodeActionKind.QuickFix),
            ResolveProvider = false
        };

    public Task<CommandOrCodeActionContainer?> Handle(
        CodeActionParams    request,
        CancellationToken   cancellationToken)
    {
        var uri = request.TextDocument.Uri;

        if (!IsFeatureFile(uri))
        {
            _logger.LogVerbose($"FeatureCodeActionHandler: ignoring non-.feature URI {uri}");
            return Task.FromResult<CommandOrCodeActionContainer?>(null);
        }

        // Resolve the match set for the feature file's primary owner.
        var primaryOwner = _scopeManager.ResolvePrimaryOwner(uri);
        var matchKey = primaryOwner is not null
            ? new MatchSetKey(uri.ToString(),
                new ProjectOwner(primaryOwner.ProjectFullName, primaryOwner.TargetFrameworkMoniker))
            : MatchSetKey.ForUnknownProject(uri.ToString());

        _matchService.TryGet(matchKey, out var matchSet);

        var allUndefined = matchSet?.Undefined.ToList() ?? new List<LSP.Core.Matching.StepBindingMatch>();
        if (allUndefined.Count == 0)
        {
            _logger.LogVerbose($"FeatureCodeActionHandler: no undefined steps for {uri}");
            return Task.FromResult<CommandOrCodeActionContainer?>(null);
        }

        // Read skeleton style from project config.
        var configProvider = _scopeManager.GetConfigurationProviderForUri(uri);
        var config = configProvider.GetConfiguration();
        var style  = config?.SnippetExpressionStyle ?? SnippetExpressionStyle.CucumberExpression;
        var csharpConfig = new CSharpCodeGenerationConfiguration();

        // Determine target file metadata.
        var featurePath   = uri.GetFileSystemPath();
        var className     = StepDefinitionFileBuilder.ClassNameFromFeaturePath(featurePath);
        var defaultNs     = primaryOwner?.DefaultNamespace ?? Path.GetFileNameWithoutExtension(featurePath);
        var projectFolder = primaryOwner?.ProjectFolder ?? Path.GetDirectoryName(featurePath) ?? string.Empty;
        var bindingPaths  = primaryOwner is not null
            ? _scopeManager.GetBindingFilePathsForProject(primaryOwner)
            : (IReadOnlyCollection<string>)Array.Empty<string>();
        var targetFolder  = FindBestTargetFolder(bindingPaths, featurePath);
        var targetPath = Path.Combine(targetFolder, className + ".cs");
        if (File.Exists(targetPath))
        {
            int suffix = 2;
            while (File.Exists(Path.Combine(targetFolder, className + suffix + ".cs")))
                suffix++;
            targetPath = Path.Combine(targetFolder, className + suffix + ".cs");
        }
        className = Path.GetFileNameWithoutExtension(targetPath);
        var @namespace    = StepDefinitionFileBuilder.DeriveNamespace(projectFolder, defaultNs, targetPath);

        const string indent  = "    ";
        var          newLine = Environment.NewLine;

        // Collect actions.
        var actions = new List<CommandOrCodeAction>();

        // ── "Define all missing steps in file" ─────────────────────────────────
        if (allUndefined.Count >= 1)
        {
            var action = BuildAction(
                title:          allUndefined.Count == 1
                    ? "Define missing step"
                    : "Define all missing steps in file",
                steps:          allUndefined,
                style:          style,
                csharpConfig:   csharpConfig,
                className:      className,
                @namespace:     @namespace,
                targetPath:     targetPath,
                indent:         indent,
                newLine:        newLine);

            if (action is not null) actions.Add(new CommandOrCodeAction(action));
        }

        // ── Per-step actions (when cursor is on a step in the request range) ───
        var stepsInRange = allUndefined
            .Where(s => OverlapsRange(s, request.Range, matchSet))
            .ToList();

        if (stepsInRange.Count == 1 && stepsInRange[0] != allUndefined[0])
        {
            // Only add individual action if it's different from the "all" action above.
            var singleStep = stepsInRange[0];
            var stepText = GetStepText(singleStep);
            var singleAction = BuildAction(
                title:          $"Define step: {stepText}",
                steps:          new[] { singleStep },
                style:          style,
                csharpConfig:   csharpConfig,
                className:      className,
                @namespace:     @namespace,
                targetPath:     targetPath,
                indent:         indent,
                newLine:        newLine);

            if (singleAction is not null)
                actions.Insert(0, new CommandOrCodeAction(singleAction));
        }

        _logger.LogVerbose($"FeatureCodeActionHandler: {actions.Count} action(s) for {uri}");
        return Task.FromResult<CommandOrCodeActionContainer?>(
            new CommandOrCodeActionContainer(actions));
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private CodeAction? BuildAction(
        string                            title,
        IEnumerable<LSP.Core.Matching.StepBindingMatch> steps,
        SnippetExpressionStyle            style,
        CSharpCodeGenerationConfiguration csharpConfig,
        string                            className,
        string                            @namespace,
        string                            targetPath,
        string                            indent,
        string                            newLine)
    {
        var descriptors = _scaffoldService.BuildDescriptors(steps, style);
        if (descriptors.Count == 0) return null;

        var snippets = descriptors
            .Select(d => StepSkeletonRenderer.Render(d, indent, newLine))
            .ToList();

        var fileContent = StepDefinitionFileBuilder.BuildNewFile(
            snippets, className, @namespace, csharpConfig, indent, newLine);

        var targetUri = DocumentUri.FromFileSystemPath(targetPath);

        var edit = new WorkspaceEdit
        {
            DocumentChanges = new Container<WorkspaceEditDocumentChange>(
                new WorkspaceEditDocumentChange(new CreateFile
                {
                    Uri     = targetUri,
                    Options = new CreateFileOptions { IgnoreIfExists = true }
                }),
                new WorkspaceEditDocumentChange(new TextDocumentEdit
                {
                    TextDocument = new OptionalVersionedTextDocumentIdentifier
                    {
                        Uri     = targetUri,
                        Version = null
                    },
                    Edits = new TextEditContainer(new TextEdit
                    {
                        Range   = new LspRange(new Position(0, 0), new Position(0, 0)),
                        NewText = fileContent
                    })
                }))
        };

        return new CodeAction
        {
            Title       = title,
            Kind        = CodeActionKind.QuickFix,
            Edit        = edit,
            IsPreferred = true
        };
    }

    private static bool IsFeatureFile(DocumentUri uri) =>
        uri.Path.EndsWith(".feature", StringComparison.OrdinalIgnoreCase);

    private static bool OverlapsRange(
        LSP.Core.Matching.StepBindingMatch step,
        LspRange                           requestRange,
        LSP.Core.Matching.FeatureBindingMatchSet? matchSet)
    {
        if (matchSet is null) return false;
        // The match set maps character offsets; LSP range is line/character.
        // Use a conservative overlap: include the step if it appears anywhere in the document.
        // A more precise line-based overlap would require the document snapshot, which is
        // not available here without additional plumbing. Returning true includes all steps,
        // and per-step narrowing is deferred to full implementation.
        return true;
    }

    private static string GetStepText(LSP.Core.Matching.StepBindingMatch step)
    {
        var item = step.Result.Items.FirstOrDefault(
            i => i.Type == LSP.Core.Bindings.MatchResultType.Undefined);
        return item?.UndefinedStep?.StepText ?? string.Empty;
    }

    /// <summary>
    /// Picks the best target directory for a new step-definition file.
    /// Prefers the folder that already holds the most binding files (so the generated file
    /// lands alongside the user's existing step definitions), then falls back to a sibling
    /// StepDefinitions/ folder or the feature file's own directory.
    /// </summary>
    private static string FindBestTargetFolder(
        IReadOnlyCollection<string> bindingFiles,
        string featureFilePath)
    {
        if (bindingFiles.Count > 0)
        {
            var best = bindingFiles
                .Select(p => Path.GetDirectoryName(p) ?? string.Empty)
                .Where(d => d.Length > 0)
                .GroupBy(d => d, StringComparer.OrdinalIgnoreCase)
                .OrderByDescending(g => g.Count())
                .FirstOrDefault();
            if (best is not null)
                return best.Key;
        }

        var featureDir    = Path.GetDirectoryName(featureFilePath) ?? string.Empty;
        var siblingStepDefs = Path.Combine(featureDir, "StepDefinitions");
        return Directory.Exists(siblingStepDefs) ? siblingStepDefs : featureDir;
    }
}
