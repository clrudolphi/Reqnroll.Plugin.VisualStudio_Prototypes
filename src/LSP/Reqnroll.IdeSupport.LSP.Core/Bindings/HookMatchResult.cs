
namespace Reqnroll.IdeSupport.LSP.Core.Bindings;
public class HookMatchResult
{
    public ProjectHookBinding[] Items { get; }

    public bool HasHooks => Items.Length > 0;

    public HookMatchResult(ProjectHookBinding[] items)
    {
        Items = items;
    }
}
