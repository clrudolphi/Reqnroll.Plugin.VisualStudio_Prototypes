#nullable enable

using OmniSharp.Extensions.LanguageServer.Protocol;
using OmniSharp.Extensions.LanguageServer.Protocol.Client.Capabilities;
using OmniSharp.Extensions.LanguageServer.Protocol.Document;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using Reqnroll.IdeSupport.Common.Diagnostics;
using Reqnroll.IdeSupport.LSP.Core.Discovery;
using Reqnroll.IdeSupport.LSP.Core.Document;
using Reqnroll.IdeSupport.LSP.Core.Editor.Completions;
using Reqnroll.IdeSupport.LSP.Core.Editor.Completions.Matching;
using Reqnroll.IdeSupport.LSP.Core.Matching;
using Reqnroll.IdeSupport.LSP.Server.Discovery;
using Reqnroll.IdeSupport.LSP.Server.Document;
using Reqnroll.IdeSupport.LSP.Server.Services;
using Reqnroll.IdeSupport.LSP.Server.Workspace;
using LspRange = OmniSharp.Extensions.LanguageServer.Protocol.Models.Range;

namespace Reqnroll.IdeSupport.LSP.Server.Handlers.ProtocolHandlers;

/// <summary>
/// Handles <c>textDocument/completion</c> requests for <c>*.feature</c> files.
/// Implements both F7 (Gherkin keyword completion) and F8 (step-definition sample completion).
/// Registered via OmniSharp dynamic registration (<see cref="ICompletionHandler"/>), scoped to
/// <c>**/*.feature</c> documents so it does not conflict with the C# language server.
/// </summary>
public sealed class GherkinCompletionHandler : ICompletionHandler
{
    private readonly ICompletionContextResolver    _contextResolver;
    private readonly ICompletionService            _completionService;
    private readonly ICompletionMatcher            _matcher;
    private readonly IBindingMatchService          _matchService;
    private readonly IDocumentBufferService        _bufferService;
    private readonly ILspWorkspaceScopeManager     _scopeManager;
    private readonly IProjectBindingRegistryLookup _registryLookup;
    private readonly ClientIdeContext              _clientIde;
    private readonly IDeveroomLogger               _logger;

    public GherkinCompletionHandler(
        ICompletionContextResolver    contextResolver,
        ICompletionService            completionService,
        ICompletionMatcher            matcher,
        IBindingMatchService          matchService,
        IDocumentBufferService        bufferService,
        ILspWorkspaceScopeManager     scopeManager,
        IProjectBindingRegistryLookup registryLookup,
        ClientIdeContext              clientIde,
        IDeveroomLogger               logger)
    {
        _contextResolver   = contextResolver;
        _completionService = completionService;
        _matcher           = matcher;
        _matchService      = matchService;
        _bufferService     = bufferService;
        _scopeManager      = scopeManager;
        _registryLookup    = registryLookup;
        _clientIde         = clientIde;
        _logger            = logger;
    }

    public CompletionRegistrationOptions GetRegistrationOptions(
        CompletionCapability capability,
        ClientCapabilities   clientCapabilities)
        => new()
        {
            DocumentSelector = new TextDocumentSelector(
                new TextDocumentFilter { Pattern = "**/*.feature" }),
            ResolveProvider  = false
        };

    public Task<CompletionList> Handle(CompletionParams request, CancellationToken cancellationToken)
    {
        var uri = request.TextDocument.Uri;

        if (!IsFeatureFile(uri))
        {
            _logger.LogVerbose($"GherkinCompletionHandler: ignoring non-.feature URI {uri}");
            return Task.FromResult(new CompletionList());
        }

        if (!_bufferService.TryGet(uri, out var buffer) || buffer is null)
        {
            _logger.LogVerbose($"GherkinCompletionHandler: no document buffer for {uri}");
            return Task.FromResult(new CompletionList());
        }

        var snapshot   = buffer.ToGherkinTextSnapshot();
        var cursorLine = request.Position.Line;
        var cursorChar = request.Position.Character;

        cancellationToken.ThrowIfCancellationRequested();

        var registry         = _registryLookup.GetRegistryForUri(uri);
        var fallbackLanguage = GetFallbackLanguage(uri);

        var ctx = _contextResolver.Resolve(snapshot, cursorLine, cursorChar, registry, fallbackLanguage);

        return ctx switch
        {
            StepCompletionContext    s => Task.FromResult(HandleStep(s,    uri, cursorLine, snapshot)),
            KeywordCompletionContext k => Task.FromResult(HandleKeyword(k, cursorLine, cursorChar, snapshot)),
            _                         => Task.FromResult(new CompletionList())
        };
    }

    // ── F8 ────────────────────────────────────────────────────────────────────

    private CompletionList HandleStep(
        StepCompletionContext s,
        DocumentUri          uri,
        int                  cursorLine,
        IGherkinTextSnapshot snapshot)
    {
        var owners = _scopeManager.ResolveOwners(uri);
        IReadOnlyCollection<ProjectOwner>? projectFilter = owners.Count > 0
            ? owners.Select(p => new ProjectOwner(p.ProjectFullName, p.TargetFrameworkMoniker))
                    .ToArray()
            : null;

        var registry = _registryLookup.GetRegistryForUri(uri);

        Func<ProjectStepDefinitionBinding, int> usageCounter = sd =>
            sd.Implementation?.SourceLocation is { } loc
                ? _matchService.FindUsages(loc, projectFilter).Count
                : 0;

        var result = _completionService.GetStepCompletions(
            s.Step, s.TypedAfterKeyword, registry, usageCounter, _matcher);

        var snapshotLine = snapshot.GetLineFromLineNumber(cursorLine);
        var lineLength   = snapshotLine.End - snapshotLine.Start;
        var stepRange    = new LspRange(
            new Position(cursorLine, s.StepTextStartColumn),
            new Position(cursorLine, lineLength));

        _logger.LogVerbose(
            $"GherkinCompletionHandler: {result.Entries.Count} step completion(s) for {uri}");
        return new CompletionList(ToItems(result.Entries, stepRange));
    }

    // ── F7 ────────────────────────────────────────────────────────────────────

    private CompletionList HandleKeyword(
        KeywordCompletionContext k,
        int                     cursorLine,
        int                     cursorChar,
        IGherkinTextSnapshot    snapshot)
    {
        // Replacement range: first non-whitespace → end of current word + trailing whitespace
        var lineText = snapshot.GetLineFromLineNumber(cursorLine).GetText();

        // A partial table row like "|4" causes the Gherkin AST to fall through to keyword
        // suggestions (including @tags, block keywords), which mangle the row when Tab accepts
        // the top completion. Suppress keyword completions for table rows entirely.
        if (lineText.TrimStart().StartsWith("|", StringComparison.Ordinal))
        {
            _logger.LogVerbose("GherkinCompletionHandler: table row — suppressing keyword completions");

            if (_clientIde.IsVisualStudio)
            {
                // VS 2022 treats an empty CompletionList for a trigger-character request as
                // "reject and revert the typed character" — returning [] would delete the '|'
                // from the document. Offer a no-op cell-separator item instead.
                var insertPos = new Position(cursorLine, cursorChar);
                return new CompletionList(new[]
                {
                    new CompletionItem
                    {
                        Label    = "| ",
                        Detail   = "Table cell separator",
                        Kind     = (CompletionItemKind)(int)CompletionEntryKind.Keyword,
                        TextEdit = new TextEditOrInsertReplaceEdit(new TextEdit
                        {
                            Range   = new LspRange(insertPos, insertPos),
                            NewText = "| "
                        })
                    }
                });
            }

            return new CompletionList();
        }

        var kwResult = k.ExpectedTokens.Length > 0
            ? _completionService.GetKeywordCompletions(k.ExpectedTokens, k.Dialect)
            : _completionService.GetDefaultKeywordCompletions(k.Dialect);
        var kwStart  = 0;
        while (kwStart < lineText.Length && char.IsWhiteSpace(lineText[kwStart]))
            kwStart++;
        var kwEnd = lineText.Length;
        while (kwEnd > kwStart && char.IsWhiteSpace(lineText[kwEnd - 1]))
            kwEnd--;
        var kwRange = new LspRange(
            new Position(cursorLine, kwStart),
            new Position(cursorLine, kwEnd));

        _logger.LogVerbose(
            $"GherkinCompletionHandler: {kwResult.Entries.Count} keyword completion(s)");
        return new CompletionList(ToItems(kwResult.Entries, kwRange));
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private string GetFallbackLanguage(DocumentUri uri)
    {
        var configProvider = _scopeManager.GetConfigurationProviderForUri(uri);
        return configProvider.GetConfiguration()?.DefaultFeatureLanguage ?? "en";
    }

    private static List<CompletionItem> ToItems(IReadOnlyList<CompletionEntry> entries, LspRange range)
        => entries
            .Select(e => new CompletionItem
            {
                Label      = e.Label,
                Detail     = e.Detail,
                Kind       = (CompletionItemKind)(int)e.Kind,
                SortText   = e.SortText,
                FilterText = e.FilterText,
                TextEdit   = new TextEditOrInsertReplaceEdit(new TextEdit
                {
                    Range   = range,
                    NewText = e.InsertText ?? e.Label
                })
            })
            .ToList();

    private static bool IsFeatureFile(DocumentUri uri) =>
        uri.Path.EndsWith(".feature", StringComparison.OrdinalIgnoreCase);
}
