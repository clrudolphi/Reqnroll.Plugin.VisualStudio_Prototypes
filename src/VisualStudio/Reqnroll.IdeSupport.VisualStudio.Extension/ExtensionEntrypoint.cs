using Microsoft.Extensions.DependencyInjection;
using Microsoft.VisualStudio.Extensibility;
using Reqnroll.IdeSupport.VisualStudio.Extension.CommentToggle;
using Reqnroll.IdeSupport.VisualStudio.Extension.FindStepUsages;
using Reqnroll.IdeSupport.VisualStudio.Extension.FindUnusedStepDefinitions;
using Reqnroll.IdeSupport.VisualStudio.Extension.GoToHooks;
using Reqnroll.IdeSupport.VisualStudio.Extension.LspInterception;
using Reqnroll.IdeSupport.VisualStudio.Extension.RenameStep;
using Reqnroll.IdeSupport.VisualStudio.Extension.StepCodeLens;
#pragma warning disable VSEXTPREVIEW_CODELENS

namespace Reqnroll.IdeSupport.VisualStudio.Extension
{
    /// <summary>
    /// Extension entrypoint for the VisualStudio.Extensibility extension.
    /// </summary>
    [VisualStudioContribution]
    internal class ExtensionEntrypoint : Microsoft.VisualStudio.Extensibility.Extension
    {
        /// <inheritdoc />
        public override ExtensionConfiguration ExtensionConfiguration => new()
        {
            RequiresInProcessHosting = true,
        };

        /// <inheritdoc />
        protected override void InitializeServices(IServiceCollection serviceCollection)
        {
            base.InitializeServices(serviceCollection);

            // Shared holder for the runtime-created F14 "Find Step Usages" components.  Registering
            // it here makes it resolvable by constructor injection in both ReqnrollLanguageClient
            // (which populates it) and FindStepUsagesCommand / future command filters (which read it),
            // rather than relying on the undocumented ability to inject one contribution class into
            // another.
            serviceCollection.AddSingleton<FindStepUsagesState>();
            serviceCollection.AddSingleton<FindUnusedStepDefinitionsState>();
            serviceCollection.AddSingleton<GoToHooksState>();
            serviceCollection.AddSingleton<StepCodeLensState>();
            serviceCollection.AddSingleton<CommentToggleState>();
            serviceCollection.AddSingleton<RenameStepState>();
            // ExtensionPart subclasses are not auto-registered by the framework; must be explicit.
            serviceCollection.AddSingleton<StepCodeLensProvider>();

            // Owns the LSP server process + duplex pipe. Registered as a singleton so that
            // constructor-injecting it into ReqnrollLanguageClient (constructed by VS.Extensibility
            // at extension load, before any .feature file is open) triggers eager process launch —
            // see LspServerConnectionService's remarks for the full rationale.
            serviceCollection.AddSingleton<LspServerConnectionService>();
        }
    }
}
