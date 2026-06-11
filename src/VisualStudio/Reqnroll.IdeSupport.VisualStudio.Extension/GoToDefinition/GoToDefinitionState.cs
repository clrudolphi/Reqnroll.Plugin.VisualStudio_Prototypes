#nullable enable

namespace Reqnroll.IdeSupport.VisualStudio.Extension.GoToDefinition;

/// <summary>
/// Container-registered singleton holder for the runtime-created F5 "Go to Definition" service.
/// </summary>
/// <remarks>
/// <see cref="GoToDefinitionService"/> depends on <c>LspInterceptingPipe</c>, which only exists after
/// the language server connection is established — too late for plain DI construction.
/// <see cref="ReqnrollLanguageClient"/> populates this on server init and clears it on dispose;
/// <see cref="GoToDefinitionCommand"/> reads it via constructor injection.
/// </remarks>
internal sealed class GoToDefinitionState
{
    /// <summary>Set once the server has initialised; null before that and after dispose.</summary>
    public GoToDefinitionService? Service { get; set; }
}
