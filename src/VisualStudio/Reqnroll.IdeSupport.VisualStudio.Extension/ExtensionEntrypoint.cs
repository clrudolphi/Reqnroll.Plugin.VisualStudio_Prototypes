using Microsoft.Extensions.DependencyInjection;
using Microsoft.VisualStudio.Extensibility;
using Reqnroll.IdeSupport.VisualStudio.Extension.FindStepUsages;
using Reqnroll.IdeSupport.VisualStudio.Extension.FindUnusedStepDefinitions;
using Reqnroll.IdeSupport.VisualStudio.Extension.GoToDefinition;
using Reqnroll.IdeSupport.VisualStudio.Extension.GoToHooks;
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
            serviceCollection.AddSingleton<GoToDefinitionState>();
            serviceCollection.AddSingleton<StepCodeLensState>();
            // ExtensionPart subclasses are not auto-registered by the framework; must be explicit.
            serviceCollection.AddSingleton<StepCodeLensProvider>();
        }
    }
}
