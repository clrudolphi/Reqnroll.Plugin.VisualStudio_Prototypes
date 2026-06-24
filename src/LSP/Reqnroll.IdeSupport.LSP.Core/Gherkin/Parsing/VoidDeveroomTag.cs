using Reqnroll.IdeSupport.LSP.Core.Documents;

namespace Reqnroll.IdeSupport.LSP.Core.Gherkin.Parsing;

public record VoidDeveroomTag : DeveroomTag
{
    public static VoidDeveroomTag Instance = new();

    private VoidDeveroomTag() : base("Void", GherkinRange.Empty, new object())
    {
    }

    internal override DeveroomTag AddChild(DeveroomTag childTag)
    {
        childTag.ParentTag = this;
        return childTag;
    }
}
