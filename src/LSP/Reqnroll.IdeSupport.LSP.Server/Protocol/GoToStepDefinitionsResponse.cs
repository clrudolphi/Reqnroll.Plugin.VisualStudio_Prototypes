#nullable enable

using System.Collections.Generic;
using Newtonsoft.Json;

namespace Reqnroll.IdeSupport.LSP.Server.Protocol;

/// <summary>
/// Response DTO for the custom <c>reqnroll/goToStepDefinitions</c> request (F5 — Go to Step Definition).
/// Returns all step-definition bindings that match the step at the queried position, with enough
/// metadata (step type, method name) to produce a labelled picker when more than one binding matches.
/// </summary>
public sealed class GoToStepDefinitionsResponse
{
    [JsonProperty("stepDefinitions")]
    public List<GoToStepDefinitionLocation> StepDefinitions { get; set; } = new();
}

/// <summary>One matching step-definition binding returned by the server.</summary>
public sealed class GoToStepDefinitionLocation
{
    /// <summary>C# source file URI (e.g. <c>file:///C:/project/Steps.cs</c>).</summary>
    [JsonProperty("uri")]
    public string Uri { get; set; } = "";

    /// <summary>0-based line of the step-definition method declaration in the source file.</summary>
    [JsonProperty("startLine")]
    public int StartLine { get; set; }

    /// <summary>0-based character of the step-definition method declaration in the source file.</summary>
    [JsonProperty("startChar")]
    public int StartChar { get; set; }

    /// <summary>Step keyword type (e.g. <c>"Given"</c>, <c>"When"</c>, <c>"Then"</c>).</summary>
    [JsonProperty("stepType")]
    public string StepType { get; set; } = "";

    /// <summary>
    /// Qualified C# method name of the step definition (e.g. <c>"CalculatorSteps.AddTwoNumbers"</c>).
    /// </summary>
    [JsonProperty("methodName")]
    public string MethodName { get; set; } = "";
}
