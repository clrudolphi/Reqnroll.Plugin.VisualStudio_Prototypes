using OmniSharp.Extensions.LanguageServer.Protocol;
using Reqnroll.IdeSupport.LSP.Server.Workspace;

namespace Reqnroll.IdeSupport.LSP.Server.Discovery;

/// <summary>
/// Applies an immediate, source-level (Roslyn) binding update for a single <c>.cs</c> file,
/// patching the owning project's binding registry so step matches update as the user types —
/// before any build. See <see cref="CSharpBindingDiscoveryService"/> and design doc feature F2.
/// </summary>
public interface ICSharpBindingDiscoveryService
{
    /// <summary>
    /// Re-discovers the bindings declared in <paramref name="text"/> (the current contents of the
    /// <c>.cs</c> document at <paramref name="uri"/>) and replaces that file's entries in its
    /// project's binding registry. No-ops when the document has no owning project or the project
    /// has no binding provider yet.
    /// </summary>
    Task UpdateFromSourceAsync(DocumentUri uri, string text, CancellationToken cancellationToken);

    /// <summary>
    /// Re-discovers the bindings declared in <paramref name="text"/> for a <em>known</em>
    /// <paramref name="project"/> and replaces that file's entries in the project's binding
    /// registry — <b>without</b> consulting the membership index (<c>ResolveOwners</c>).
    /// <para>
    /// Used during Connector startup reconciliation, where the membership baseline may not have
    /// arrived yet (so index lookups would return no owners) but the owning project is already
    /// known to the caller. No-ops when the project has no binding provider yet.
    /// </para>
    /// </summary>
    Task UpdateFromSourceForProjectAsync(
        LspReqnrollProject project, string filePath, string text, CancellationToken cancellationToken);
}
