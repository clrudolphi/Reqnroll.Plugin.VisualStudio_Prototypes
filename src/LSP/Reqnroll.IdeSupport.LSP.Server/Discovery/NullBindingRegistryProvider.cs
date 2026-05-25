using Reqnroll.IdeSupport.LSP.Core.Discovery;

namespace Reqnroll.IdeSupport.LSP.Server.Discovery;

/// <summary>
/// Stub <see cref="IBindingRegistryProvider"/> that always returns
/// <see cref="ProjectBindingRegistry.Invalid"/>. Used until a real
/// discovery connector is wired in.
/// </summary>
public sealed class NullBindingRegistryProvider : IBindingRegistryProvider
{
    public ProjectBindingRegistry Current => ProjectBindingRegistry.Invalid;

    /// <inheritdoc/>
    /// <remarks>Never raised by this implementation.</remarks>
    public event EventHandler? BindingRegistryChanged
    {
        add    { /* no-op */ }
        remove { /* no-op */ }
    }
}
