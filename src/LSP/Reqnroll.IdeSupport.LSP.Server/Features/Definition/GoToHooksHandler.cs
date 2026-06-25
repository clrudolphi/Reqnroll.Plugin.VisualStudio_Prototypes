#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using OmniSharp.Extensions.LanguageServer.Protocol;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using Reqnroll.IdeSupport.Common.Diagnostics;
using Reqnroll.IdeSupport.LSP.Core.Bindings;


using Reqnroll.IdeSupport.LSP.Core.Gherkin.Parsing;


using Reqnroll.IdeSupport.LSP.Server.Discovery;
using Reqnroll.IdeSupport.LSP.Server.Document;
using Reqnroll.IdeSupport.LSP.Server.Protocol;
using Reqnroll.IdeSupport.LSP.Server.Features.TextSync;
using Reqnroll.IdeSupport.LSP.Server.Services;
using Reqnroll.IdeSupport.LSP.Server.Workspace;

namespace Reqnroll.IdeSupport.LSP.Server.Features.Definition;

/// <summary>
/// Handles the custom <c>reqnroll/goToHooks</c> request (F17 — Hook Navigation).
/// <para>
/// Given a cursor position in a <c>.feature</c> file, returns all hook bindings that are
/// applicable at that position, filtered by context level (Feature / Scenario / Step) and
/// any tag/scope expressions on the hook.
/// </para>
/// <para>
/// A separate custom message is used rather than reusing <c>textDocument/definition</c>
/// because that message is already used by F5 (Go to Step Definition) on step lines;
/// the server cannot distinguish the two intents from position alone, and step-level hooks
/// (<c>[BeforeStep]</c> / <c>[AfterStep]</c>) would be unreachable.
/// </para>
/// </summary>
public sealed class GoToHooksHandler
{
    // Hook types visible at each context level, per design doc F17.
    private static readonly IReadOnlySet<HookType> FeatureLevelHooks = new HashSet<HookType>
    {
        HookType.BeforeTestRun,  HookType.AfterTestRun,
        HookType.BeforeFeature,  HookType.AfterFeature,
    };

    private static readonly IReadOnlySet<HookType> ScenarioLevelHooks = new HashSet<HookType>(FeatureLevelHooks)
    {
        HookType.BeforeScenario, HookType.AfterScenario,
    };

    private static readonly IReadOnlySet<HookType> StepLevelHooks = new HashSet<HookType>(ScenarioLevelHooks)
    {
        HookType.BeforeScenarioBlock, HookType.AfterScenarioBlock,
        HookType.BeforeStep,          HookType.AfterStep,
    };

    private readonly IDocumentBufferService        _bufferService;
    private readonly IProjectBindingRegistryLookup _registryLookup;
    private readonly IDeveroomLogger               _logger;
    private readonly ILspTelemetryService?          _telemetryService;

    public GoToHooksHandler(
        IDocumentBufferService        bufferService,
        IProjectBindingRegistryLookup registryLookup,
        IDeveroomLogger               logger,
        ILspTelemetryService?         telemetryService = null)
    {
        _bufferService  = bufferService;
        _registryLookup = registryLookup;
        _logger         = logger;
        _telemetryService = telemetryService;
    }

    public Task<GoToHooksResponse> HandleAsync(
        TextDocumentPositionParams request,
        CancellationToken          cancellationToken)
    {
        var uri = request.TextDocument.Uri;

        if (!IsFeatureFile(uri))
        {
            _logger.LogVerbose($"GoToHooksHandler: ignoring non-.feature URI {uri}");
            return Task.FromResult(new GoToHooksResponse());
        }

        if (!_bufferService.TryGet(uri, out var buffer) || buffer is null)
        {
            _logger.LogVerbose($"GoToHooksHandler: no document buffer for {uri}");
            return Task.FromResult(new GoToHooksResponse());
        }

        if (buffer.Tags is null || buffer.Tags.Count == 0)
        {
            _logger.LogVerbose($"GoToHooksHandler: tags not yet computed for {uri}");
            return Task.FromResult(new GoToHooksResponse());
        }

        var snapshot = buffer.ToGherkinTextSnapshot();
        var offset   = snapshot.ToOffset(request.Position.Line, request.Position.Character);

        var (level, contextTag) = ResolveContext(buffer.Tags, offset);
        if (level == HookContextLevel.None)
        {
            _logger.LogVerbose($"GoToHooksHandler: no Gherkin context at offset {offset} in {uri}");
            return Task.FromResult(new GoToHooksResponse());
        }

        var registry = _registryLookup.GetRegistryForUri(uri);
        if (ReferenceEquals(registry, ProjectBindingRegistry.Invalid))
        {
            _logger.LogVerbose($"GoToHooksHandler: no binding registry available for {uri}");
            return Task.FromResult(new GoToHooksResponse());
        }

        var applicableTypes = GetApplicableHookTypes(level);

        // ProjectHookBinding.Match does not use the Scenario argument — it only uses the
        // IGherkinDocumentContext for tag/scope matching — so null is safe to pass here.
        var hooks = registry.Hooks
            .Where(h => h.IsValid && applicableTypes.Contains(h.HookType))
            .Where(h => h.Match(null!, contextTag))
            .OrderBy(h => h.HookType)
            .ThenBy(h => h.HookOrder)
            .ToArray();

        _logger.LogVerbose($"GoToHooksHandler: {hooks.Length} hook(s) at offset {offset} in {uri}");

        var locations = new List<GoToHookLocation>(hooks.Length);
        foreach (var hook in hooks)
        {
            var loc = ToLocation(hook);
            if (loc is not null)
                locations.Add(loc);
        }

        // Telemetry
        _telemetryService?.SendEvent("GoToHook command executed", new());

        return Task.FromResult(new GoToHooksResponse { Hooks = locations });
    }

    // ── Position context resolution ───────────────────────────────────────────

    /// <summary>
    /// Determines the Gherkin context level at <paramref name="offset"/> from the flat
    /// <paramref name="tags"/> collection (produced by <c>DeveroomTagParser</c>) and returns
    /// the deepest matching tag for use as an <c>IGherkinDocumentContext</c> in scope matching.
    /// </summary>
    private static (HookContextLevel level, DeveroomTag contextTag) ResolveContext(
        IReadOnlyCollection<DeveroomTag> tags, int offset)
    {
        // Check from innermost to outermost: Step → Scenario → Feature.
        // A StepBlock hit means we're on a step line — use the enclosing ScenarioDefinitionBlock
        // as context because steps carry no tags; only scenario and feature blocks do.
        var stepTag = FindTag(tags, DeveroomTagTypes.StepBlock, offset);
        if (stepTag is not null)
        {
            var enclosingScenario = FindTag(tags, DeveroomTagTypes.ScenarioDefinitionBlock, offset);
            return (HookContextLevel.Step, enclosingScenario ?? stepTag);
        }

        var scenarioTag = FindTag(tags, DeveroomTagTypes.ScenarioDefinitionBlock, offset);
        if (scenarioTag is not null)
            return (HookContextLevel.Scenario, scenarioTag);

        var featureTag = FindTag(tags, DeveroomTagTypes.FeatureBlock, offset);
        if (featureTag is not null)
            return (HookContextLevel.Feature, featureTag);

        return (HookContextLevel.None, null!);
    }

    private static DeveroomTag? FindTag(IReadOnlyCollection<DeveroomTag> tags, string type, int offset)
        => tags.FirstOrDefault(t => t.Type == type && ContainsOffset(t, offset));

    private static bool ContainsOffset(DeveroomTag tag, int offset)
        => offset >= tag.Range.Start && offset < tag.Range.End;

    // ── Hook applicability ────────────────────────────────────────────────────

    private static IReadOnlySet<HookType> GetApplicableHookTypes(HookContextLevel level) =>
        level switch
        {
            HookContextLevel.Feature  => FeatureLevelHooks,
            HookContextLevel.Scenario => ScenarioLevelHooks,
            HookContextLevel.Step     => StepLevelHooks,
            _                          => throw new ArgumentOutOfRangeException(nameof(level))
        };

    // ── Location conversion ───────────────────────────────────────────────────

    private static GoToHookLocation? ToLocation(ProjectHookBinding hook)
    {
        var src = hook.Implementation?.SourceLocation;
        if (src is null || string.IsNullOrEmpty(src.SourceFile))
            return null;

        // SourceLocation is 1-based; response uses 0-based (LSP convention).
        return new GoToHookLocation
        {
            Uri        = DocumentUri.FromFileSystemPath(src.SourceFile).ToString(),
            StartLine  = src.SourceFileLine   - 1,
            StartChar  = src.SourceFileColumn - 1,
            HookType   = hook.HookType.ToString(),
            HookOrder  = hook.HookOrder,
            MethodName = hook.Implementation?.Method ?? string.Empty,
        };
    }

    private static bool IsFeatureFile(DocumentUri uri) =>
        uri.Path.EndsWith(".feature", StringComparison.OrdinalIgnoreCase);

    // ── Nested types ──────────────────────────────────────────────────────────

    internal enum HookContextLevel { None, Feature, Scenario, Step }
}
