using Gherkin.Ast;
using Reqnroll.IdeSupport.Common;
using System;
using System.Linq;

namespace Reqnroll.IdeSupport.LSP.Core.Gherkin.Parsing;

public class DeveroomGherkinDocument : GherkinDocument
{
    private readonly List<int> _statesForLines;

    public DeveroomGherkinDocument(Feature feature, IEnumerable<Comment> comments, string sourceFilePath,
        GherkinDialect gherkinDialect, List<int> statesForLines) : base(feature, comments)
    {
        _statesForLines = statesForLines;
        GherkinDialect = gherkinDialect;
    }

    public GherkinDialect GherkinDialect { get; }

    public TokenType[] GetExpectedTokens(int line, IMonitoringService monitoringService)
    {
        if (_statesForLines.Count <= line)
            return new TokenType[0];

        var state = _statesForLines[line];
        if (state < 0)
            return new TokenType[0];
        return DeveroomGherkinParser.GetExpectedTokens(state, monitoringService);
    }
}
