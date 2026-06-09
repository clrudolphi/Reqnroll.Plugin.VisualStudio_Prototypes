#nullable enable

using System.Collections.Generic;
using Newtonsoft.Json;

namespace Reqnroll.IdeSupport.LSP.Server.Protocol;

/// <summary>
/// Response DTO for the custom <c>reqnroll/findStepUsages</c> request (F14 P2b).
/// <para>
/// Carries the full three-state contract that <c>textDocument/references</c> cannot express
/// because OmniSharp's <c>LocationOrLocationLinks</c> serializer cannot return <c>null</c>:
/// <list type="bullet">
///   <item><see cref="IsBinding"/> = <see langword="false"/> → caret is not on a binding; client falls through to built-in C# FAR.</item>
///   <item><see cref="IsBinding"/> = <see langword="true"/>, <see cref="Locations"/> empty → binding present, 0 usages; client shows "0 usages".</item>
///   <item><see cref="IsBinding"/> = <see langword="true"/>, <see cref="Locations"/> non-empty → the matching feature-file steps.</item>
/// </list>
/// Note: the handler never returns JSON <c>null</c> — OmniSharp's <c>OnRequest</c> framework
/// sends an error response for null returns from custom-method handlers instead of serialising
/// JSON null, so <see cref="IsBinding"/> = <see langword="false"/> is used as the sentinel.
/// </para>
/// </summary>
public sealed class FindStepUsagesResponse
{
    /// <summary>
    /// <see langword="true"/> when the queried position is on a step-definition binding.
    /// <see langword="false"/> when the position is not on a binding — the client should fall
    /// through to built-in C# Find All References.
    /// </summary>
    [JsonProperty("isBinding")]
    public bool IsBinding { get; set; }

    /// <summary>
    /// The feature-file steps that match this binding.
    /// Empty when <see cref="IsBinding"/> is <see langword="true"/> but no steps match ("0 usages").
    /// </summary>
    [JsonProperty("locations")]
    public List<FindStepUsageItem> Locations { get; set; } = new();
}

/// <summary>One step-usage location within a feature file.</summary>
public sealed class FindStepUsageItem
{
    /// <summary>Feature-file document URI (e.g. <c>file:///C:/project/calc.feature</c>).</summary>
    [JsonProperty("uri")]
    public string Uri { get; set; } = "";

    /// <summary>0-based start line of the step text span.</summary>
    [JsonProperty("startLine")]
    public int StartLine { get; set; }

    /// <summary>0-based start character of the step text span.</summary>
    [JsonProperty("startChar")]
    public int StartChar { get; set; }

    /// <summary>0-based end line of the step text span.</summary>
    [JsonProperty("endLine")]
    public int EndLine { get; set; }

    /// <summary>0-based end character of the step text span.</summary>
    [JsonProperty("endChar")]
    public int EndChar { get; set; }

    /// <summary>
    /// The step text as it appears in the feature file (trimmed), e.g.
    /// <c>"the first number is 50"</c> (keyword excluded — matches the Range in the match cache).
    /// Populated from the document snapshot stored in the match cache at parse time.
    /// <see langword="null"/> only if the snapshot text is unavailable (should not occur in practice).
    /// </summary>
    [JsonProperty("stepText")]
    public string? StepText { get; set; }

    /// <summary>
    /// The trimmed Gherkin keyword for this step (e.g. <c>"Given"</c>, <c>"When"</c>,
    /// <c>"Then"</c>, <c>"And"</c>). Populated from the AST at match-cache-build time.
    /// </summary>
    [JsonProperty("keyword")]
    public string? Keyword { get; set; }

    /// <summary>
    /// The name of the enclosing scenario or scenario outline
    /// (e.g. <c>"Add two numbers"</c>). <see langword="null"/> for Background steps.
    /// </summary>
    [JsonProperty("scenarioName")]
    public string? ScenarioName { get; set; }

    /// <summary>
    /// The short project name (file-name-without-extension of the .csproj) that owns the
    /// feature file in which this step appears (e.g. <c>"Minimal"</c>, <c>"Minimalnet481"</c>).
    /// </summary>
    [JsonProperty("projectName")]
    public string? ProjectName { get; set; }
}
