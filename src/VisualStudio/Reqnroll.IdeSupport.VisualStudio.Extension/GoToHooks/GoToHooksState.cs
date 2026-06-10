#nullable enable

namespace Reqnroll.IdeSupport.VisualStudio.Extension.GoToHooks;

/// <summary>
/// Container-registered singleton holder for the runtime-created F17 "Go to Hooks" service.
/// </summary>
/// <remarks>
/// <see cref="GoToHooksService"/> depends on <c>LspInterceptingPipe</c>, which only exists after
/// the language server connection is established — too late for plain DI construction.
/// <see cref="ReqnrollLanguageClient"/> populates this on server init and clears it on dispose;
/// <see cref="GoToHooksCommand"/> reads it via constructor injection.
/// </remarks>
internal sealed class GoToHooksState
{
    /// <summary>Set once the server has initialised; null before that and after dispose.</summary>
    public GoToHooksService? Service { get; set; }
}
