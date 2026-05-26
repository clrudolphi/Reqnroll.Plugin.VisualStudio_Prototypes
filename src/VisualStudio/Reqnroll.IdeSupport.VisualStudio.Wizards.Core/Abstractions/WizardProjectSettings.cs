namespace Reqnroll.IdeSupport.VisualStudio.Wizards.Abstractions;

/// <summary>
/// A self-contained, minimal project settings model for use within the
/// wizard layer. In the VS integration layer VsWizardContext populates
/// this from the full ProjectSettings record. In the new LSP extension
/// it will be populated from whatever project system is available there.
/// </summary>
public class WizardProjectSettings
{
    public static readonly WizardProjectSettings Uninitialized = new();

    public bool IsReqnrollProject { get; init; }
    public bool IsSpecFlowProject { get; init; }
    public bool DesignTimeFeatureFileGenerationEnabled { get; init; }
    public bool HasDesignTimeGenerationReplacement { get; init; }
    public bool HasXUnitAdapter { get; init; }
    public string? ReqnrollVersionLabel { get; init; }

    /// <summary>
    /// Friendly label used for telemetry — mirrors ProjectSettings.GetShortLabel().
    /// </summary>
    public string ShortLabel { get; init; } = string.Empty;
}
