using Reqnroll.IdeSupport.LSP.Core.Bindings;

namespace Reqnroll.IdeSupport.LSP.Server.Specs.Support;

/// <summary>Per-scenario state for the binding-discovery specs.</summary>
public sealed class DiscoveryContext
{
    /// <summary>
    /// When non-null the scenario needs the full <c>SampleProjectGenerator</c> toolchain
    /// (a specific Reqnroll version / target framework / test runner / platform / custom
    /// connector that the prebuilt fixture cannot represent) and is skipped where that
    /// toolchain is not provisioned.
    /// </summary>
    public string? GeneratorReason { get; private set; }

    public ProjectBindingRegistry? Registry { get; set; }

    public void RequireGenerator(string reason) => GeneratorReason ??= reason;
}
