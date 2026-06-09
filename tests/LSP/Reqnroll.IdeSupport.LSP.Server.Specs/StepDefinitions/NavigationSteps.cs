using AwesomeAssertions;
using OmniSharp.Extensions.LanguageServer.Protocol;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using Reqnroll;
using Reqnroll.IdeSupport.LSP.Server.Protocol;
using Reqnroll.IdeSupport.LSP.Server.Specs.Support;

namespace Reqnroll.IdeSupport.LSP.Server.Specs.StepDefinitions;

[Binding]
public sealed class NavigationSteps
{
    private readonly LspScenarioContext _ctx;

    public NavigationSteps(LspScenarioContext ctx) => _ctx = ctx;

    [When(@"references are requested at line (\d+) column (\d+) in ""(.*)""")]
    public async Task WhenReferencesAreRequestedAt(int line, int column, string fileName)
    {
        var uri = _ctx.UriFor(fileName);
        _ctx.LastReferences = await _ctx.Harness.Client
            .RequestReferencesAsync(uri, line, column)
            .ConfigureAwait(false);
    }

    [Then(@"(\d+) reference(?:s are|s is| is| are) returned")]
    public void ThenNReferencesAreReturned(int expected)
    {
        if (expected == 0)
        {
            (_ctx.LastReferences is null || !_ctx.LastReferences.Any())
                .Should().BeTrue($"expected 0 references but got {_ctx.LastReferences?.Count()}");
        }
        else
        {
            _ctx.LastReferences.Should().NotBeNull("references should have been returned");
            _ctx.LastReferences!.Count().Should().Be(expected);
        }
    }

    [Then(@"the references include a location in ""(.*)""")]
    public void ThenTheReferencesIncludeALocationIn(string fileName)
    {
        _ctx.LastReferences.Should().NotBeNull("references should have been returned");
        _ctx.LastReferences!.Should().Contain(
            loc => loc.Location!.Uri.ToString()
                       .EndsWith(fileName, StringComparison.OrdinalIgnoreCase),
            $"a reference to '{fileName}' should be present");
    }

    // ── reqnroll/findStepUsages (three-state custom request) ──────────────────

    [When(@"step usages are requested at line (\d+) column (\d+) in ""(.*)""")]
    public async Task WhenStepUsagesAreRequestedAt(int line, int column, string fileName)
    {
        var uri = _ctx.UriFor(fileName);
        _ctx.LastFindStepUsages = await _ctx.Harness.Client
            .RequestFindStepUsagesAsync(uri, line, column)
            .ConfigureAwait(false);
    }

    [Then(@"the step usages response has isBinding false")]
    public void ThenStepUsagesResponseIsNotBinding()
    {
        _ctx.LastFindStepUsages.Should().NotBeNull();
        _ctx.LastFindStepUsages!.IsBinding.Should().BeFalse(
            "isBinding=false signals 'not a binding' — client should fall through to built-in C# FAR");
    }

    [Then(@"the step usages response has isBinding true")]
    public void ThenStepUsagesResponseIsBinding()
    {
        _ctx.LastFindStepUsages.Should().NotBeNull("server should return a response for a binding position");
        _ctx.LastFindStepUsages!.IsBinding.Should().BeTrue();
    }

    [Then(@"(\d+) step usage(?:s are|s is| is| are) returned")]
    public void ThenNStepUsagesAreReturned(int expected)
    {
        _ctx.LastFindStepUsages.Should().NotBeNull();
        _ctx.LastFindStepUsages!.Locations.Should().HaveCount(expected);
    }

    [Then(@"the step usages include a location in ""(.*)""")]
    public void ThenStepUsagesIncludeALocationIn(string fileName)
    {
        _ctx.LastFindStepUsages.Should().NotBeNull();
        _ctx.LastFindStepUsages!.Locations.Should().Contain(
            loc => loc.Uri.EndsWith(fileName, StringComparison.OrdinalIgnoreCase),
            $"a step usage in '{fileName}' should be present");
    }

    [Then(@"the step usages include a non-empty step text")]
    public void ThenStepUsagesIncludeNonEmptyStepText()
    {
        _ctx.LastFindStepUsages.Should().NotBeNull();
        _ctx.LastFindStepUsages!.Locations.Should().Contain(
            loc => !string.IsNullOrWhiteSpace(loc.StepText),
            "at least one location should carry step text extracted from the in-memory snapshot");
    }

}
