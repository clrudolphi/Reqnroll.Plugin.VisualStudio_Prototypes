using System.Collections.Generic;

namespace Reqnroll.IdeSupport.VisualStudio.Extension.FindStepUsages;

/// <summary>
/// Three-state result from <see cref="FindStepUsagesService.FindUsagesAsync"/>:
/// <list type="bullet">
///   <item><see cref="NotABinding"/> — caret is not on any step-definition binding; Surface 3 should fall through to the built-in command.</item>
///   <item><see cref="IsBinding"/> with <c>Locations.Count == 0</c> — binding present but no matching steps; show "0 usages" window.</item>
///   <item><see cref="IsBinding"/> with <c>Locations.Count > 0</c> — matching feature-file steps; show them in the results window.</item>
/// </list>
/// </summary>
internal sealed class StepUsagesResult
{
    /// <summary>Sentinel: the queried position is not a step-definition binding.</summary>
    public static readonly StepUsagesResult NotABinding = new StepUsagesResult();

    private readonly IReadOnlyList<StepUsageLocation>? _locations;

    // Private constructor for the NotABinding sentinel.
    private StepUsagesResult() { }

    /// <summary>Creates a result for a binding (zero or more usages).</summary>
    public StepUsagesResult(IReadOnlyList<StepUsageLocation> locations)
    {
        _locations = locations;
    }

    public bool IsBinding => _locations is not null;

    public IReadOnlyList<StepUsageLocation> Locations =>
        _locations ?? System.Array.Empty<StepUsageLocation>();
}
