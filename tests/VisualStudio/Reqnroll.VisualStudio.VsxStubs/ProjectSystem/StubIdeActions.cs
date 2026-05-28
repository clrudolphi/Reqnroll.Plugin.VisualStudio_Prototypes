#nullable disable

namespace Reqnroll.VisualStudio.VsxStubs.ProjectSystem;

public class StubIdeActions : IIdeActions
{
    private readonly StubIdeScope _ideScope;

    public SourceLocation LastNavigateToSourceLocation;
    public string LastShowContextMenuHeader;
    // Deferred: ContextMenuItem, IAsyncContextMenu, QuestionDescription not yet ported
    // public List<ContextMenuItem> LastShowContextMenuItems;
    // public QuestionDescription LastShowQuestion { get; set; }

    public string ClipboardText { get; private set; }
    public bool IsComplete { get; private set; }

    public StubIdeActions(IIdeScope ideScope)
    {
        _ideScope = (StubIdeScope) ideScope;
    }

    public bool NavigateTo(SourceLocation sourceLocation)
    {
        _ideScope.Logger.LogInfo("IDE Action performed");
        LastNavigateToSourceLocation = sourceLocation;

        var view = _ideScope.EnsureOpenTextView(sourceLocation);
        _ideScope.CurrentTextView = view;
        return true;
    }

    public void ShowError(string description, Exception exception)
    {
        _ideScope.Logger.LogException(_ideScope.MonitoringService, exception, description);
    }

    public void ShowProblem(string description)
    {
        _ideScope.Logger.LogWarning($"User Notification: {description}");
    }

    public void SetClipboardText(string text)
    {
        ClipboardText = text;
    }

    public void ResetMock()
    {
        LastNavigateToSourceLocation = null;
        LastShowContextMenuHeader = null;
    }
}
