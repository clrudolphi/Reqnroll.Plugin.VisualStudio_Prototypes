#nullable enable

using Reqnroll.IdeSupport.Common.Configuration;
using Reqnroll.IdeSupport.LSP.Core.Bindings;
using Reqnroll.IdeSupport.LSP.Core.Matching;

namespace Reqnroll.IdeSupport.LSP.Core.Editor.Scaffolding;

/// <summary>
/// Collects undefined steps from the match cache and produces a deduplicated list of
/// <see cref="StepSkeletonDescriptor"/> objects using <see cref="StepSkeletonRenderer"/>.
/// </summary>
public sealed class StepScaffoldService : IStepScaffoldService
{
    public IReadOnlyList<StepSkeletonDescriptor> BuildDescriptors(
        IEnumerable<StepBindingMatch> undefinedSteps,
        SnippetExpressionStyle        style)
    {
        var seen   = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<StepSkeletonDescriptor>();

        foreach (var match in undefinedSteps)
        {
            // Each undefined step carries at least one Undefined MatchResultItem.
            foreach (var item in match.Result.Items)
            {
                if (item.Type != MatchResultType.Undefined) continue;
                if (item.UndefinedStep is not { } step) continue;

                var descriptor = StepSkeletonRenderer.BuildDescriptor(step, style);

                // Dedup by (Block, expression).
                if (seen.Add(descriptor.DeduplicationKey))
                    result.Add(descriptor);
            }
        }

        // Order: Given < When < Then, then by appearance (insertion order preserved within block).
        return result
            .OrderBy(d => (int)d.Block)
            .ToList()
            .AsReadOnly();
    }
}
