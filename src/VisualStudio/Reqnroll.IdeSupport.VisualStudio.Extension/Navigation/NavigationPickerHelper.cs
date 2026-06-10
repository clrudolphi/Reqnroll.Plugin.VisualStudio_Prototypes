#nullable enable

using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using EnvDTE80;
using Microsoft.VisualStudio.Shell;
using Reqnroll.IdeSupport.Common.Diagnostics;

namespace Reqnroll.IdeSupport.VisualStudio.Extension.Navigation;

/// <summary>
/// Shared helper: show a picker when a navigation query returns multiple results, then open
/// the chosen file in the editor. Works for Go to Hooks (F17) and future ambiguous-step-
/// definition navigation (F5 — multiple matching bindings).
/// </summary>
/// <remarks>
/// 0 targets → no-op (caller logs this case).
/// 1 target  → navigates directly without a picker.
/// N targets → shows a <see cref="NavigationPickerDialog"/>; navigates to the selection.
/// </remarks>
internal static class NavigationPickerHelper
{
    public static async Task PickAndNavigateAsync(
        IReadOnlyList<NavigationTarget> targets,
        IDeveroomLogger                 logger,
        string                          promptTitle,
        CancellationToken               cancellationToken)
    {
        if (targets.Count == 0)
            return;

        NavigationTarget chosen;

        if (targets.Count == 1)
        {
            chosen = targets[0];
        }
        else
        {
            // Dialog must be created and shown on the UI thread.
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);

            var dialog = new NavigationPickerDialog(promptTitle, targets);
            if (dialog.ShowModal() != true || dialog.SelectedIndex < 0)
            {
                logger.LogVerbose("NavigationPickerHelper: picker dismissed, no navigation.");
                return;
            }

            chosen = targets[dialog.SelectedIndex];
        }

        await NavigateToAsync(chosen, logger, cancellationToken).ConfigureAwait(false);
    }

    private static async Task NavigateToAsync(
        NavigationTarget  target,
        IDeveroomLogger   logger,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(target.FilePath))
        {
            logger.LogWarning($"NavigationPickerHelper: file not found: '{target.FilePath}'");
            return;
        }

        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);

        var dte = ServiceProvider.GlobalProvider.GetService(typeof(EnvDTE.DTE)) as DTE2;
        if (dte is null)
        {
            logger.LogWarning("NavigationPickerHelper: DTE service not available.");
            return;
        }

        var window = dte.ItemOperations.OpenFile(target.FilePath, EnvDTE.Constants.vsViewKindTextView);
        window.Activate();

        if (window.Document?.Selection is EnvDTE.TextSelection selection)
        {
            // NavigationTarget uses 0-based coordinates; DTE TextSelection is 1-based.
            selection.MoveToLineAndOffset(target.StartLine + 1, target.StartChar + 1);
        }

        logger.LogInfo(
            $"NavigationPickerHelper: navigated to '{target.DisplayText}' at {target.FilePath}:{target.StartLine + 1}");
    }
}
