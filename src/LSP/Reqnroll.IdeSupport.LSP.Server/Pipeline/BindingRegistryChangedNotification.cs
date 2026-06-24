using MediatR;
using Reqnroll.IdeSupport.LSP.Server.Workspace;

namespace Reqnroll.IdeSupport.LSP.Server.Pipeline;

/// <summary>
/// Published when a project's <see cref="Reqnroll.IdeSupport.LSP.Core.Bindings.ProjectBindingRegistry"/>
/// is replaced after a successful connector discovery run (e.g. triggered by a build or a
/// <c>reqnroll.json</c> change).
/// </summary>
/// <remarks>
/// When <see cref="IsFullReplacement"/> is <see langword="true"/> (e.g. startup or a post-build
/// reflection discovery run), consumers should re-parse <em>all</em> workspace feature files that
/// belong to <see cref="Project"/> — not only the currently open ones — so that the binding match
/// cache covers the complete workspace for features such as Find Usages (F14).
/// When <see cref="IsFullReplacement"/> is <see langword="false"/> (incremental Roslyn re-discovery
/// on a <c>.cs</c> save), re-parsing only the open feature files is sufficient.
/// </remarks>
public record BindingRegistryChangedNotification(
    LspReqnrollProject Project,
    bool IsFullReplacement = false) : INotification;
