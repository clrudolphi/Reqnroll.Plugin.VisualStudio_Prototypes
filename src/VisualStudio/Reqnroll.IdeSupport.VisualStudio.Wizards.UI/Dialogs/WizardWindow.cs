using System.Windows.Navigation;
using Reqnroll.IdeSupport.VisualStudio.Wizards.UI.Controls;

namespace Reqnroll.IdeSupport.VisualStudio.Wizards.UI.Dialogs;

/// <summary>
/// Base class for all wizard dialogs. Has NO VS SDK dependency.
/// Provides the same drag-to-move and link-clicked behaviour as the
/// original DialogWindow, but uses plain WPF ShowDialog() for hosting.
///
/// In the VsIntegration layer, VsWizardDialogService overrides hosting
/// by calling WindowHelper.ShowModal() from Microsoft.VisualStudio.Shell.
/// </summary>
public class WizardWindow : Window
{
    public event EventHandler<RequestNavigateEventArgs>? LinkClicked;

    protected override void OnInitialized(EventArgs e)
    {
        base.OnInitialized(e);
        AddHandler(MarkDownTextBlock.LinkClickedEvent,
            new RequestNavigateEventHandler(OnLinkClicked));
        AddHandler(MouseLeftButtonDownEvent,
            new MouseButtonEventHandler(OnMouseLeftButtonDown));
    }

    protected virtual void OnLinkClicked(object sender, RequestNavigateEventArgs e)
    {
        e.Handled = true;
        try { Process.Start(e.Uri.ToString()); } catch { /* best-effort */ }
        LinkClicked?.Invoke(sender, e);
    }

    public void OnMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        DragMove();
    }

    protected void MinimizeButton_OnClick(object sender, RoutedEventArgs e) =>
        WindowState = WindowState.Minimized;

    protected void MaximizeButton_OnClick(object sender, RoutedEventArgs e) =>
        WindowState = WindowState == WindowState.Normal ? WindowState.Maximized : WindowState.Normal;

    /// <summary>
    /// Shows the window. When running inside VS, VsWizardDialogService
    /// calls WindowHelper.ShowModal() instead of this method.
    /// </summary>
    public bool? ShowModal() => ShowDialog();
}
