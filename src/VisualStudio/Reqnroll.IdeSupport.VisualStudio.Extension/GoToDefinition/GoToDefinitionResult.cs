#nullable enable

using System.Collections.Generic;

namespace Reqnroll.IdeSupport.VisualStudio.Extension.GoToDefinition;

/// <summary>
/// Parsed result of a <c>reqnroll/goToStepDefinitions</c> response for a step in a feature file.
/// </summary>
internal sealed class GoToDefinitionResult
{
    public static readonly GoToDefinitionResult Empty = new(new List<StepDefinitionLocation>());

    public IReadOnlyList<StepDefinitionLocation> Locations { get; }

    public GoToDefinitionResult(IReadOnlyList<StepDefinitionLocation> locations)
    {
        Locations = locations;
    }
}

/// <summary>One matching step-definition binding location returned by the server.</summary>
internal sealed class StepDefinitionLocation
{
    public string Uri        { get; }
    public int    StartLine  { get; }
    public int    StartChar  { get; }
    /// <summary>e.g. "Given", "When", "Then"</summary>
    public string StepType   { get; }
    /// <summary>Qualified C# method name, e.g. "CalculatorSteps.AddTwoNumbers".</summary>
    public string MethodName { get; }

    public StepDefinitionLocation(
        string uri, int startLine, int startChar,
        string stepType, string methodName)
    {
        Uri        = uri;
        StartLine  = startLine;
        StartChar  = startChar;
        StepType   = stepType;
        MethodName = methodName;
    }
}
