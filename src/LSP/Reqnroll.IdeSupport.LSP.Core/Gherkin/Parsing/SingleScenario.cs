#nullable disable

using Gherkin.Ast;

namespace Reqnroll.IdeSupport.LSP.Core.Gherkin.Parsing;

public class SingleScenario : Scenario
{
    public SingleScenario(IEnumerable<Tag> tags, Location location, string keyword, string name, string description, IEnumerable<Step> steps,
        IEnumerable<Examples> examples = null) : base(tags, location, keyword, name, description, steps,
        examples ?? new Examples[0])
    {
    }
}
