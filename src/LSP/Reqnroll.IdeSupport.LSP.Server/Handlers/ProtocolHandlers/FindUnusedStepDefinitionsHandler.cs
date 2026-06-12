#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Reqnroll.IdeSupport.Common.Diagnostics;
using Reqnroll.IdeSupport.LSP.Core.Discovery;
using Reqnroll.IdeSupport.LSP.Core.Matching;
using Reqnroll.IdeSupport.LSP.Server.Discovery;
using Reqnroll.IdeSupport.LSP.Server.Protocol;

namespace Reqnroll.IdeSupport.LSP.Server.Handlers.ProtocolHandlers;

/// <summary>
/// Handles the custom <c>reqnroll/findUnusedStepDefinitions</c> request (F15).
/// <para>
/// Scans all project binding registries workspace-wide and returns one row per unused
/// <em>binding expression</em>. A C# method decorated with multiple step attributes produces
/// multiple <see cref="ProjectStepDefinitionBinding"/> objects; each expression is checked
/// independently — used expressions are omitted, unused ones each produce a row.
/// </para>
/// <para>
/// Uses <see cref="IBindingMatchService.FindUsages"/> with no project filter (intersection
/// semantics: an expression is considered used if any project's feature files reference it).
/// A per-location cache avoids redundant match-set scans for multi-expression methods.
/// Bindings with the same (file, line, expression) in multiple registries are deduplicated.
/// </para>
/// </summary>
public sealed class FindUnusedStepDefinitionsHandler
{
    private readonly IProjectBindingRegistryLookup _registryLookup;
    private readonly IBindingMatchService           _matchService;
    private readonly IDeveroomLogger                _logger;

    public FindUnusedStepDefinitionsHandler(
        IProjectBindingRegistryLookup registryLookup,
        IBindingMatchService          matchService,
        IDeveroomLogger               logger)
    {
        _registryLookup = registryLookup;
        _matchService   = matchService;
        _logger         = logger;
    }

    /// <summary>Finds all step-definition binding expressions with zero usages across the workspace.</summary>
    /// <remarks>
    /// A C# method may carry multiple step attributes ([Given("A")][When("B")]). Each attribute is
    /// a separate <see cref="ProjectStepDefinitionBinding"/> with its own <c>Expression</c>. The
    /// method produces one FAR row per UNUSED expression — so a method with one used and one unused
    /// expression yields a single row (the unused one). A method all of whose expressions are used
    /// produces no rows; a method none of whose expressions are used produces one row per expression.
    ///
    /// Cross-project deduplication: the same source file linked into multiple project registries
    /// would produce duplicate rows for the same expression; these are suppressed by the seen set.
    /// </remarks>
    public Task<FindUnusedStepDefinitionsResponse> HandleAsync(CancellationToken cancellationToken)
    {
        var allRegistries = _registryLookup.GetAllRegistries();
        _logger.LogVerbose(
            $"FindUnusedStepDefinitionsHandler: scanning {allRegistries.Count} project(s)");

        // Dedup by (sourceFile, sourceLine, expression): a linked .cs file appearing in N project
        // registries must not produce N copies of the same row.
        var seen = new HashSet<(string File, int Line, string Expression)>();

        // Cache per source-location FindUsages results: a multi-expression method shares one
        // SourceLocation, so the underlying match-set scan runs exactly once per location.
        var locUsagesCache = new Dictionary<(string File, int Line), IReadOnlyList<StepBindingMatch>>();

        var items = new List<UnusedStepDefinitionItem>();

        foreach (var (projectName, _, registry) in allRegistries)
        {
            if (registry == ProjectBindingRegistry.Invalid) continue;

            foreach (var sd in registry.StepDefinitions)
            {
                if (!sd.IsValid) continue;

                var loc = sd.Implementation?.SourceLocation;
                if (loc is null) continue;

                var expression = sd.Expression ?? string.Empty;
                var fileKey    = (loc.SourceFile ?? string.Empty).ToLowerInvariant();
                var seenKey    = (fileKey, loc.SourceFileLine, expression);
                if (!seen.Add(seenKey)) continue;

                // FindUsages returns all feature steps whose matched binding lives at `loc`
                // (any expression on the method). Cache per location to avoid redundant scans.
                var locKey = (fileKey, loc.SourceFileLine);
                if (!locUsagesCache.TryGetValue(locKey, out var locUsages))
                {
                    locUsages = _matchService.FindUsages(loc, null);
                    locUsagesCache[locKey] = locUsages;
                }

                // Filter to usages of THIS expression specifically: a step's MatchedStepDefinition
                // records the exact binding that matched it, so we can distinguish expression A from
                // expression B even though both share the same SourceLocation.
                var isExpressionUsed = locUsages.Any(usage =>
                    usage.Result.Items.Any(i => i.MatchedStepDefinition?.Expression == expression));

                if (isExpressionUsed) continue;

                var (className, methodName) = ParseMethod(sd.Implementation!.Method);

                items.Add(new UnusedStepDefinitionItem
                {
                    ProjectName       = projectName,
                    ClassName         = className,
                    MethodName        = methodName,
                    BindingExpression = sd.Expression,
                    SourceFile        = loc.SourceFile,
                    SourceLine        = loc.SourceFileLine - 1,   // 1-based → 0-based
                    SourceChar        = loc.SourceFileColumn - 1, // 1-based → 0-based
                });
            }
        }

        _logger.LogVerbose(
            $"FindUnusedStepDefinitionsHandler: found {items.Count} unused step definition(s)");

        return Task.FromResult(new FindUnusedStepDefinitionsResponse { Items = items });
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Parses ClassName and MethodName from the stored Method string.
    /// <list type="bullet">
    ///   <item>Connector path: <c>"ClassName.MethodName(paramType1, paramType2)"</c></item>
    ///   <item>Roslyn path: <c>"Namespace.ClassName.MethodName"</c></item>
    /// </list>
    /// In both cases: strip params, split on <c>.</c>, last segment = MethodName,
    /// second-to-last = ClassName.
    /// </summary>
    internal static (string ClassName, string MethodName) ParseMethod(string? method)
    {
        if (string.IsNullOrEmpty(method) || method == "???")
            return ("(unknown)", "(unknown)");

        // Strip parameter list: "ClassName.MethodName(int, string)" → "ClassName.MethodName"
        var parenIdx     = method!.IndexOf('(');
        var withoutParams = parenIdx >= 0 ? method.Substring(0, parenIdx) : method;

        var parts = withoutParams.Split('.');
        if (parts.Length == 1)
            return ("(unknown)", parts[0]);

        // Second-to-last = ClassName (handles both Roslyn multi-segment and connector forms)
        var methodName = parts[parts.Length - 1];
        var className  = parts[parts.Length - 2];
        return (className, methodName);
    }
}
