using Reqnroll.IdeSupport.Common.Configuration;
using Reqnroll.IdeSupport.Common.ProjectSystem.Configuration;

namespace Reqnroll.IdeSupport.LSP.Core.Editor.Services.Formatting;

/// <summary>
/// Resolved formatting settings for a single document format pass.
/// </summary>
public class GherkinFormatSettings
{
    public GherkinFormatConfiguration Configuration { get; set; } = new();

    public string Indent { get; set; } = "    ";

    public int FeatureChildrenIndentLevel => Configuration.IndentFeatureChildren ? 1 : 0;
    public int RuleChildrenIndentLevelWithinRule => Configuration.IndentRuleChildren ? 1 : 0;
    public int StepIndentLevelWithinStepContainer => Configuration.IndentSteps ? 1 : 0;
    public int AndStepIndentLevelWithinSteps => Configuration.IndentAndSteps ? 1 : 0;
    public int DataTableIndentLevelWithinStep => Configuration.IndentDataTable ? 1 : 0;
    public int DocStringIndentLevelWithinStep => Configuration.IndentDocString ? 1 : 0;
    public int ExamplesBlockIndentLevelWithinScenarioOutline => Configuration.IndentExamples ? 1 : 0;
    public int ExamplesTableIndentLevelWithinExamplesBlock => Configuration.IndentExamplesTable ? 1 : 0;
    public string TableCellPadding => new(' ', Configuration.TableCellPaddingSize);
    public bool RightAlignNumericTableCells => Configuration.TableCellRightAlignNumericContent;

    /// <summary>
    /// Builds format settings from LSP <c>FormattingOptions</c>, applying editorconfig overrides
    /// and any Reqnroll-specific configuration from <paramref name="configuration"/>.
    /// </summary>
    public static GherkinFormatSettings FromLspOptions(
        int tabSize, bool insertSpaces,
        IEditorConfigOptionsProvider editorConfigOptionsProvider,
        string filePath,
        DeveroomConfiguration? configuration)
    {
        var gherkinFormatConfig = configuration?.Editor?.GherkinFormat?.Clone() ?? new GherkinFormatConfiguration();

        var editorConfigOptions = editorConfigOptionsProvider.GetEditorConfigOptionsByPath(filePath);
        editorConfigOptions.UpdateFromEditorConfig(gherkinFormatConfig);

        var indent = insertSpaces ? new string(' ', tabSize) : "\t";

        return new GherkinFormatSettings
        {
            Indent = indent,
            Configuration = gherkinFormatConfig
        };
    }
}
