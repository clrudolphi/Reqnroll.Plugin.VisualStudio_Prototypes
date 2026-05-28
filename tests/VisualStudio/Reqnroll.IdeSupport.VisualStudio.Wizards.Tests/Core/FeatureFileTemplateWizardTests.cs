namespace Reqnroll.IdeSupport.VisualStudio.Wizards.Tests.Core;

public class FeatureFileTemplateWizardTests
{
    private readonly IWizardContext _context = Substitute.For<IWizardContext>();
    private readonly IWizardTelemetry _telemetry = Substitute.For<IWizardTelemetry>();

    private FeatureFileTemplateWizard CreateSut() => new();

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
    public void RunStarted_fires_OnFeatureFileAdded_telemetry()
    {
        var settings = WizardProjectSettings.Uninitialized;
        SetupContext(settings);

        CreateSut().RunStarted(_context);

        _telemetry.Received(1).OnFeatureFileAdded(settings);
    }

    [Fact]
    public void RunStarted_sets_CustomTool_when_design_time_generation_enabled()
    {
        SetupContext(new WizardProjectSettings
        {
            IsReqnrollProject = true,
            DesignTimeFeatureFileGenerationEnabled = true
        });

        CreateSut().RunStarted(_context);

        _context.ReplacementsDictionary[WizardContextKeys.CustomToolSettingKey]
            .Should().Be(FeatureFileTemplateWizard.CustomToolReqnrollSingleFileGenerator);
    }

    [Fact]
    public void RunStarted_shows_problem_when_no_msbuild_generation_and_no_design_time()
    {
        SetupContext(new WizardProjectSettings
        {
            IsReqnrollProject = true,
            DesignTimeFeatureFileGenerationEnabled = false,
            HasDesignTimeGenerationReplacement = false
        });

        CreateSut().RunStarted(_context);

        _context.Received(1).ShowProblem(Arg.Is<string>(s =>
            s.Contains(FeatureFileTemplateWizard.ReqnrollToolsMsBuildGenerationPackageName)));
    }

    [Fact]
    public void RunStarted_does_not_show_problem_when_design_time_generation_replacement_exists()
    {
        SetupContext(new WizardProjectSettings
        {
            IsReqnrollProject = true,
            DesignTimeFeatureFileGenerationEnabled = false,
            HasDesignTimeGenerationReplacement = true
        });

        CreateSut().RunStarted(_context);

        _context.DidNotReceive().ShowProblem(Arg.Any<string>());
    }

    [Fact]
    public void RunStarted_sets_BuildAction_for_xUnit_adapter()
    {
        SetupContext(new WizardProjectSettings
        {
            IsReqnrollProject = true,
            HasXUnitAdapter = true
        });

        CreateSut().RunStarted(_context);

        _context.ReplacementsDictionary[WizardContextKeys.BuildActionKey]
            .Should().Be(FeatureFileTemplateWizard.BuildActionReqnrollEmbeddedFeature);
    }

    [Fact]
    public void RunStarted_does_not_modify_replacements_for_non_reqnroll_project()
    {
        SetupContext(new WizardProjectSettings { IsReqnrollProject = false });

        CreateSut().RunStarted(_context);

        _context.ReplacementsDictionary.Should().BeEmpty();
        _context.DidNotReceive().ShowProblem(Arg.Any<string>());
    }
}
