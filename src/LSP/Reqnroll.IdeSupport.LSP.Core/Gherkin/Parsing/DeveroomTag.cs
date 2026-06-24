#nullable disable

using Reqnroll;
using Reqnroll.IdeSupport.LSP.Core.Bindings;
using Reqnroll.IdeSupport.LSP.Core.Documents;

namespace Reqnroll.IdeSupport.LSP.Core.Gherkin.Parsing;

public record DeveroomTag(string Type, GherkinRange Range, object Data = null) : IGherkinDocumentContext
{
    private readonly List<DeveroomTag> _childTags = new();

    public DeveroomTag ParentTag { get; protected internal set; }
    public ICollection<DeveroomTag> ChildTags => _childTags;
    public bool IsError => Type.EndsWith("Error");

    IGherkinDocumentContext IGherkinDocumentContext.Parent => ParentTag;
    object IGherkinDocumentContext.Node => Data;

    internal virtual DeveroomTag AddChild(DeveroomTag childTag)
    {
        childTag.ParentTag = this;
        _childTags.Add(childTag);
        return childTag;
    }

    public override string ToString() => $"{Type}:{Range}";

    public IEnumerable<DeveroomTag> GetDescendantsOfType(string type)
    {
        foreach (var childTag in ChildTags)
        {
            if (childTag.Type == type)
                yield return childTag;

            foreach (var descendantTag in childTag.GetDescendantsOfType(type)) yield return descendantTag;
        }
    }
}
