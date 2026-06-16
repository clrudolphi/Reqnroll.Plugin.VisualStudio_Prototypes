namespace Reqnroll.IdeSupport.LSP.Server.Protocol;

/// <summary>
/// Payload for the <c>reqnroll/refreshCodeLens</c> server-to-client notification.
/// </summary>
/// <remarks>
/// The server pushes this to the Visual Studio client after a full binding-registry replacement
/// (startup connector discovery, post-build, or membership-baseline arrival) so the VS client can
/// invalidate its already-rendered C# step code lenses and re-pull fresh usage counts. This is the
/// VS equivalent of the standard <c>workspace/codeLens/refresh</c> request, which VS cannot route to
/// our pipe-based code-lens provider. Other IDE clients use <c>workspace/codeLens/refresh</c> instead
/// and ignore this notification.
/// </remarks>
public sealed class RefreshCodeLensParams
{
    /// <summary>The project whose binding registry was replaced (informational; for diagnostics).</summary>
    public string ProjectName { get; set; } = string.Empty;
}
