namespace Reqnroll.IdeSupport.VisualStudio.Wizards.Tests.Core;

public class ConfigFileTemplateWizardTests
{
    private readonly IWizardContext _context = Substitute.For<IWizardContext>();
    private readonly IWizardTelemetry _telemetry = Substitute.For<IWizardTelemetry>();

    private ConfigFileTemplateWizard CreateSut() => new();

    private void SetupContext(WizardProjectSettings settings)
    {
        var replacements = new Dictionary<string, string>();
        _context.ReplacementsDictionary.Returns(replacements);
        _context.ProjectSettings.Returns(settings);
        _context.Telemetry.Returns(_telemetry);
    }

    [Fact]
    public void RunStarted_returns_true()
    {
        SetupContext(WizardProjectSettings.Uninitialized);

        var result = CreateSut().RunStarted(_context);

        result.Should().BeTrue();
    }

    [Fact]
    public void RunStarted_fires_OnConfigFileAdded_telemetry()
    {
        var settings = WizardProjectSettings.Uninitialized;
        SetupContext(settings);

        CreateSut().RunStarted(_context);

        _telemetry.Received(1).OnConfigFileAdded(settings);
    }

    [Fact]
    public void RunStarted_sets_CopyToOutputDirectory_for_SpecFlow_project()
    {
        SetupContext(new WizardProjectSettings { IsSpecFlowProject = true });

        CreateSut().RunStarted(_context);

        _context.ReplacementsDictionary[WizardContextKeys.CopyToOutputDirectoryKey]
            .Should().Be("PreserveNewest");
    }

    [Fact]
    public void RunStarted_does_not_set_CopyToOutputDirectory_for_Reqnroll_project()
    {
        SetupContext(new WizardProjectSettings { IsReqnrollProject = true });

        CreateSut().RunStarted(_context);

        _context.ReplacementsDictionary.Should().NotContainKey(WizardContextKeys.CopyToOutputDirectoryKey);
    }
}
