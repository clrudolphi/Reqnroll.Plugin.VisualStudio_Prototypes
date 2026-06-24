

using Cucumber.TagExpressions;

namespace Reqnroll.IdeSupport.LSP.Core.Bindings.TagExpressions;
public class InvalidTagExpression : ReqnrollTagExpression, ITagExpression
{
    public string Message { get; }
    public InvalidTagExpression(ITagExpression? expression, string originalTagExpression, string message) : base(expression!, originalTagExpression)
    {
        Message = message;
    }
    public override bool Evaluate(IEnumerable<string> tags)
    {
        throw new InvalidOperationException("Cannot evaluate an invalid tag expression: " + Message);
    }
    public override string ToString()
    {
        return "Invalid Tag Expression: " + Message;
    }
}
