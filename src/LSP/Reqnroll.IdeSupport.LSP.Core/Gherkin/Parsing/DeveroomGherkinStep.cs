using Gherkin.Ast;

namespace Reqnroll.IdeSupport.LSP.Core.Gherkin.Parsing;

public class DeveroomGherkinStep : Step
{
    public DeveroomGherkinStep(Location location, string keyword, StepKeywordType keywordType, string text, StepArgument argument,
        StepKeyword stepKeyword, ScenarioBlock scenarioBlock) : base(location, keyword, keywordType, text, argument)
    {
        StepKeyword = stepKeyword;
        ScenarioBlock = scenarioBlock;
    }

    public ScenarioBlock ScenarioBlock { get; }
    public StepKeyword StepKeyword { get; }
}
