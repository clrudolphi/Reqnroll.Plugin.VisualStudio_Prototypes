
namespace Reqnroll.IdeSupport.LSP.Core.Bindings;

/// <summary>
/// Provides the current ProjectBindingRegistry and notifies when it is replaced.
/// VS implementation: thin wrapper over IDiscoveryService.BindingRegistryCache.
/// LSP implementation: populated by MSBuild/JSON connector-based discovery.
/// </summary>
public interface IBindingRegistryProvider
{
    ProjectBindingRegistry Current { get; }

    /// <summary>
    /// Raised on any thread when the registry is replaced.
    /// The <c>bool</c> argument is <see langword="true"/> for a full registry replacement
    /// (connector/reflection discovery) and <see langword="false"/> for an incremental
    /// Roslyn per-file patch.
    /// </summary>
    event EventHandler<bool> BindingRegistryChanged;
}