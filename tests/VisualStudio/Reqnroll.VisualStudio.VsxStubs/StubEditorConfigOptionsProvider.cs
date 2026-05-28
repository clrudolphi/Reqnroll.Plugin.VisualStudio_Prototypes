#if false // Deferred: IEditorConfigOptionsProvider, IEditorConfigOptions not yet ported
namespace Reqnroll.VisualStudio.VsxStubs;

public class StubEditorConfigOptionsProvider
{
    public IEditorConfigOptions GetEditorConfigOptions(IWpfTextView textView) => new NullEditorConfigOptions();
    public IEditorConfigOptions GetEditorConfigOptionsByPath(string filePath) => new NullEditorConfigOptions();
}

#endif
