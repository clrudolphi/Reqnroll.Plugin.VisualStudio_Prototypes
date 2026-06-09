#nullable enable

namespace Reqnroll.IdeSupport.VisualStudio.Extension.FindStepUsages;

/// <summary>
/// Shared, container-registered holder for the runtime-created F14 components.
/// </summary>
/// <remarks>
/// <see cref="FindStepUsagesService"/> depends on the <c>LspInterceptingPipe</c>, which only
/// exists after the language server connection is established — too late for it to be a plain
/// DI-constructed singleton.  Instead this holder is registered as a singleton in
/// <c>ExtensionEntrypoint.InitializeServices</c>; <see cref="ReqnrollLanguageClient"/> populates
/// it on server-init (and clears it on dispose), and the command / command-filter consumers inject
/// <i>this</i> rather than reaching through the entire language-server-provider contribution.
/// This keeps consumers depending only on a service we explicitly register (guaranteed resolvable),
/// not on an undocumented ability to inject one contribution class into another.
/// </remarks>
internal sealed class FindStepUsagesState
{
    /// <summary>Set once the server has initialised; null before that and after dispose.</summary>
    public FindStepUsagesService? Service { get; set; }

    /// <summary>Set once the server has initialised; null before that and after dispose.</summary>
    public FindStepUsagesRenderer? Renderer { get; set; }
}
