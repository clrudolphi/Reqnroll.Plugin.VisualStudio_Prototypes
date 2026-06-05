using AwesomeAssertions;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using Reqnroll;
using Reqnroll.IdeSupport.LSP.Server.Specs.Support;

namespace Reqnroll.IdeSupport.LSP.Server.Specs.StepDefinitions;

/// <summary>
/// Steps that drive Roslyn source-level binding discovery (design doc F2): opening / editing a
/// <c>.cs</c> step-definition document and asserting that an already-open feature file's step
/// flips between bound and unbound — without a build. "Unbound" is observable as a
/// <c>reqnroll.undefined_step</c> semantic token covering the step text; a bound step emits no
/// such token.
/// </summary>
[Binding]
public sealed class CSharpBindingSteps
{
    private const string UndefinedStepToken = "reqnroll.undefined_step";

    private readonly LspScenarioContext _ctx;
    private readonly Dictionary<string, int> _csVersions = new(StringComparer.OrdinalIgnoreCase);

    public CSharpBindingSteps(LspScenarioContext ctx) => _ctx = ctx;

    // ── When ───────────────────────────────────────────────────────────────────

    [When(@"the C# step definition file ""(.*)"" is opened with")]
    public async Task WhenTheCsharpFileIsOpenedWith(string fileName, string content)
    {
        await _ctx.EnsureStartedAsync();
        var uri = _ctx.UriFor(fileName);
        _csVersions[fileName] = 1;
        _ctx.Harness.Client.OpenCSharpDocument(uri, 1, content);
    }

    [When(@"the C# step definition file ""(.*)"" is changed to")]
    public void WhenTheCsharpFileIsChangedTo(string fileName, string content)
    {
        var uri = _ctx.UriFor(fileName);
        var version = _csVersions.TryGetValue(fileName, out var v) ? v + 1 : 2;
        _csVersions[fileName] = version;
        _ctx.Harness.Client.ChangeCSharpDocument(uri, version, content);
    }

    // ── Then ───────────────────────────────────────────────────────────────────

    [Then(@"the feature step ""(.*)"" is reported as unbound")]
    public async Task ThenTheFeatureStepIsReportedAsUnbound(string stepText)
    {
        var ok = await PollFeatureTokensAsync(tokens =>
            tokens.Any(t => IsUndefinedStepFor(t, stepText)));

        ok.Should().BeTrue(
            $"the step '{stepText}' should surface as an unbound (undefined) step after the " +
            $"binding registry change");
    }

    [Then(@"the feature step ""(.*)"" is reported as bound")]
    public async Task ThenTheFeatureStepIsReportedAsBound(string stepText)
    {
        var ok = await PollFeatureTokensAsync(tokens =>
            tokens.Count > 0 && !tokens.Any(t => IsUndefinedStepFor(t, stepText)));

        ok.Should().BeTrue(
            $"the step '{stepText}' should be matched (no undefined-step token) once a binding " +
            $"with a matching expression exists");
    }

    // ── Helpers ─────────────────────────────────────────────────────────────────

    private static bool IsUndefinedStepFor(DecodedToken token, string stepText) =>
        string.Equals(token.TokenType, UndefinedStepToken, StringComparison.OrdinalIgnoreCase) &&
        token.Text.Trim() == stepText;

    /// <summary>
    /// Re-requests semantic tokens for the most recently opened feature file and decodes them,
    /// retrying until <paramref name="predicate"/> holds or the timeout elapses. Polling is needed
    /// because the registry change propagates asynchronously (didChange → Roslyn re-discovery →
    /// BindingRegistryChanged → feature re-parse → token-cache invalidation).
    /// </summary>
    private async Task<bool> PollFeatureTokensAsync(
        Func<IReadOnlyList<DecodedToken>, bool> predicate, int timeoutMs = 5000)
    {
        _ctx.LastUri.Should().NotBeNull("a feature file should have been opened first");

        var legend = _ctx.Harness.ServerInitializeResult.Capabilities.SemanticTokensProvider!.Legend;
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);

        while (DateTime.UtcNow < deadline)
        {
            var tokens = await _ctx.Harness.Client
                .RequestSemanticTokensAsync(_ctx.LastUri!)
                .ConfigureAwait(false);

            if (tokens is not null)
            {
                var decoded = SemanticTokenDecoder.Decode(tokens, legend, _ctx.LastDocumentText);
                if (predicate(decoded))
                {
                    _ctx.LastTokens = tokens;
                    return true;
                }
            }

            await Task.Delay(75).ConfigureAwait(false);
        }

        return false;
    }
}
