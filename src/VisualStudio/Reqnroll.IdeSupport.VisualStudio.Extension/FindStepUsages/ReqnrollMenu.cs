using Microsoft.VisualStudio.Extensibility;
using Microsoft.VisualStudio.Extensibility.Commands;

namespace Reqnroll.IdeSupport.VisualStudio.Extension.FindStepUsages;

/// <summary>
/// Adds a "Reqnroll" submenu to the Extensions top-level menu.
/// Note: VS.Extensibility's <see cref="MenuConfiguration"/> does not support icons on menus —
/// the icon is carried by the child commands instead.
/// </summary>
[VisualStudioContribution]
internal static class ReqnrollMenu
{
    [VisualStudioContribution]
    public static MenuConfiguration ReqnrollExtensionsMenu => new("Reqnroll")
    {
        Placements = [CommandPlacement.KnownPlacements.ExtensionsMenu],
        Children =
        [
            MenuChild.Command<FindStepUsagesCommand>(),
        ],
    };
}
