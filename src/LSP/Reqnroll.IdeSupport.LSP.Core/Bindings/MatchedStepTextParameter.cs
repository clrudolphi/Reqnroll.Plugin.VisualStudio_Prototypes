using System;
using System.Linq;

namespace Reqnroll.IdeSupport.LSP.Core.Bindings;

public struct MatchedStepTextParameter
{
    public int Index;
    public int Length;

    public MatchedStepTextParameter(int index, int length)
    {
        Index = index;
        Length = length;
    }
}
