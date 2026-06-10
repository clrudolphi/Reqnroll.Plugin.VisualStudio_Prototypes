#nullable enable

using System.Collections.Generic;

namespace Reqnroll.IdeSupport.VisualStudio.Extension.GoToHooks;

/// <summary>
/// Parsed result of a <c>reqnroll/goToHooks</c> response.
/// </summary>
internal sealed class GoToHooksResult
{
    public static readonly GoToHooksResult Empty = new(new List<HookLocation>());

    public IReadOnlyList<HookLocation> Hooks { get; }

    public GoToHooksResult(IReadOnlyList<HookLocation> hooks)
    {
        Hooks = hooks;
    }
}

/// <summary>One applicable hook binding returned by the server.</summary>
internal sealed class HookLocation
{
    public string Uri        { get; }
    public int    StartLine  { get; }
    public int    StartChar  { get; }
    public string HookType   { get; }
    public int    HookOrder  { get; }
    public string MethodName { get; }

    public HookLocation(
        string uri, int startLine, int startChar,
        string hookType, int hookOrder, string methodName)
    {
        Uri        = uri;
        StartLine  = startLine;
        StartChar  = startChar;
        HookType   = hookType;
        HookOrder  = hookOrder;
        MethodName = methodName;
    }
}
