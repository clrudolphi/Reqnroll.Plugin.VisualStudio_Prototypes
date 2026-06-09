using Microsoft.VisualStudio.Shell;
using System;
using System.Runtime.InteropServices;
using System.Threading;
using Task = System.Threading.Tasks.Task;

namespace Reqnroll.IdeSupport.VisualStudio.Extension;

// The "Find Step Usages" command reaches the C# editor context menu by parenting to the
// shell's built-in IDG_VS_CODEWIN_NAVIGATETOLOCATION group via CommandPlacement.VsctParent,
// so this package no longer ships a compiled command table (no ProvideMenuResource needed).
[PackageRegistration(UseManagedResourcesOnly = true, AllowsBackgroundLoading = true)]
[Guid(PackageGuidString)]
public sealed class ReqnrollPluginPackage : AsyncPackage
{
    public const string PackageGuidString = "8d5fe503-e038-4079-9e45-697e0dcb3758";

    protected override async Task InitializeAsync(CancellationToken cancellationToken, IProgress<ServiceProgressData> progress)
    {
        await base.InitializeAsync(cancellationToken, progress);
    }
}
