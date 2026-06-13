using System.Text;
using System.Text.RegularExpressions;
using Gherkin.Ast;
using Reqnroll.IdeSupport.LSP.Core.Editor.Services.Parsing.GherkinDocuments;

namespace Reqnroll.IdeSupport.LSP.Core.Editor.Services.Formatting;

/// <summary>
/// Formats a parsed Gherkin document by applying consistent indentation, normalising
/// tag lines, aligning data-table cells, and replacing repeated step keywords with "And".
/// Works on a <see cref="DocumentLinesEditBuffer"/> so that callers control which lines
/// are included in the output.
/// </summary>
public class GherkinDocumentFormatter
{
    public void FormatGherkinDocument(DeveroomGherkinDocument gherkinDocument, DocumentLinesEditBuffer lines,
        GherkinFormatSettings formatSettings)
    {
        if (gherkinDocument.Feature == null)
            return;

        SetTagsAndLine(lines, gherkinDocument.Feature, string.Empty);
        SetLinesForChildren(lines, gherkinDocument.Feature.Children, formatSettings,
            formatSettings.FeatureChildrenIndentLevel, gherkinDocument.GherkinDialect);
    }

    private void SetLinesForChildren(DocumentLinesEditBuffer lines, IEnumerable<IHasLocation> children,
        GherkinFormatSettings formatSettings, int indentLevel, GherkinDialect gherkinDialect)
    {
        foreach (var featureChild in children)
        {
            SetTagsAndLine(lines, featureChild, GetIndent(formatSettings, indentLevel));

            if (featureChild is Rule rule)
                SetLinesForChildren(lines, rule.Children, formatSettings,
                    indentLevel + formatSettings.RuleChildrenIndentLevelWithinRule, gherkinDialect);

            if (featureChild is ScenarioOutline scenarioOutline)
                foreach (var example in scenarioOutline.Examples)
                {
                    var examplesBlockIndentLevel =
                        indentLevel + formatSettings.ExamplesBlockIndentLevelWithinScenarioOutline;
                    SetTagsAndLine(lines, example, GetIndent(formatSettings, examplesBlockIndentLevel));
                    FormatTable(lines, example, formatSettings,
                        examplesBlockIndentLevel + formatSettings.ExamplesTableIndentLevelWithinExamplesBlock);
                }

            if (featureChild is IHasSteps hasSteps)
                FormatSteps(lines, formatSettings, indentLevel, hasSteps, gherkinDialect);
        }
    }

    private void FormatSteps(DocumentLinesEditBuffer lines, GherkinFormatSettings formatSettings, int indentLevel,
        IHasSteps hasSteps, GherkinDialect gherkinDialect)
    {
        var previousKeyword = "";

        foreach (var step in hasSteps.Steps)
        {
            var stepIndentLevel = indentLevel + formatSettings.StepIndentLevelWithinStepContainer;
            var newKeyword = step.Keyword;

            if (step is DeveroomGherkinStep { StepKeyword: StepKeyword.And or StepKeyword.But })
            {
                stepIndentLevel += formatSettings.AndStepIndentLevelWithinSteps;
            }
            else
            {
                if (step.Keyword == previousKeyword)
                {
                    var andKeyword = GetAndKeyword(gherkinDialect);
                    newKeyword = $"{andKeyword}";
                }
                else
                    previousKeyword = step.Keyword;
            }

            SetLine(lines, step, $"{GetIndent(formatSettings, stepIndentLevel)}{newKeyword}{step.Text}");

            switch (step.Argument)
            {
                case DataTable dataTable:
                    FormatTable(lines, dataTable, formatSettings,
                        stepIndentLevel + formatSettings.DataTableIndentLevelWithinStep);
                    break;
                case DocString docString:
                    FormatDocString(lines, docString, formatSettings,
                        stepIndentLevel + formatSettings.DocStringIndentLevelWithinStep);
                    break;
            }
        }
    }

    private static string GetAndKeyword(GherkinDialect gherkinDialect) =>
        gherkinDialect.AndStepKeywords.First(k => k != GherkinDialect.AsteriskKeyword);

    private void SetTagsAndLine(DocumentLinesEditBuffer lines, IHasLocation hasLocation, string indent)
    {
        if (hasLocation is IHasTags hasTags) SetTags(lines, hasTags.Tags, indent);

        if (hasLocation is IHasDescription hasDescription)
            SetLine(lines, hasLocation, GetHasDescriptionLine(hasDescription, indent));
    }

    /// <summary>
    /// Scans <paramref name="lines"/> to find the zero-based line range of the Gherkin table
    /// that contains or is adjacent to <paramref name="cursorLine0Based"/>.
    /// Returns <see langword="null"/> when the cursor is not on a table row or on a blank
    /// line immediately following one (which occurs with the <c>\n</c> on-type trigger).
    /// </summary>
    public static (int Start, int End)? FindTableLineRange(string[] lines, int cursorLine0Based)
    {
        int anchor = -1;

        if (cursorLine0Based >= 0 && cursorLine0Based < lines.Length
            && IsTableRow(lines[cursorLine0Based]))
            anchor = cursorLine0Based;
        else if (cursorLine0Based > 0 && cursorLine0Based <= lines.Length
            && IsTableRow(lines[cursorLine0Based - 1]))
            anchor = cursorLine0Based - 1;

        if (anchor < 0) return null;

        int start = anchor;
        while (start > 0 && IsTableRow(lines[start - 1]))
            start--;

        int end = anchor;
        while (end < lines.Length - 1 && IsTableRow(lines[end + 1]))
            end++;

        return (start, end);
    }

    private static bool IsTableRow(string line) => line.TrimStart().StartsWith("|");


    /// <summary>
    /// Walks the parsed AST to find the <see cref="IHasRows"/> node (DataTable or Examples)
    /// whose first row starts at <paramref name="startLine0Based"/> (zero-based).
    /// Returns <see langword="null"/> when no table is found at that line.
    /// </summary>
    public static IHasRows? FindTableAtLine(DeveroomGherkinDocument doc, int startLine0Based)
    {
        if (doc?.Feature == null) return null;
        var targetLine1Based = startLine0Based + 1;
        return FindTableInChildren(doc.Feature.Children, targetLine1Based);
    }

    private static IHasRows? FindTableInChildren(IEnumerable<IHasLocation> children, int targetLine1Based)
    {
        foreach (var child in children)
        {
            if (child is IHasSteps hasSteps)
                foreach (var step in hasSteps.Steps)
                    if (step.Argument is DataTable dt &&
                        dt.Rows.Any(r => r.Location.Line == targetLine1Based))
                        return dt;

            if (child is ScenarioOutline outline)
                foreach (var ex in outline.Examples)
                    if (ex is IHasRows exRows &&
                        exRows.Rows.Any(r => r.Location.Line == targetLine1Based))
                        return exRows;

            if (child is Rule rule)
            {
                var result = FindTableInChildren(rule.Children, targetLine1Based);
                if (result != null) return result;
            }
        }
        return null;
    }

    internal int[] GetTableWidths(IHasRows hasRows)
    {
        var widths = new int[hasRows.Rows.Max(r => r.Cells.Count())];
        foreach (var row in hasRows.Rows)
        foreach (var item in row.Cells.Select((c, i) => new { c, i }))
            widths[item.i] = Math.Max(widths[item.i], EscapeTableCellValue(item.c.Value).Length);
        return widths;
    }

    private static string EscapeTableCellValue(string cellValue) =>
        cellValue
            .Replace("\\", "\\\\")
            .Replace("\r", "\\r")
            .Replace("\n", "\\n")
            .Replace("|", "\\|");

    private static bool IsTableCellContentNumeric(string cellValue)
    {
        if (string.IsNullOrWhiteSpace(cellValue))
            return false;
        // Regex from: https://github.com/cucumber/react-components/blob/main/src/components/gherkin/isNumber.ts#L3
        return Regex.IsMatch(cellValue.Trim(), @"^(?=.*\d.*)[-+]?\d*(?:[., ](?=\d.*)\d*)*(?:\d+E[+-]?\d+)?$");
    }

    private static bool IsTableCellContentRightAligned(string cellValue, GherkinFormatSettings formatSettings) =>
        formatSettings.RightAlignNumericTableCells && IsTableCellContentNumeric(cellValue);

    private static string? GetUnfinishedTableCell(string lineText)
    {
        var match = Regex.Match(lineText, @"(?<!\\)(\\\\)*\|(?<remaining>.*?)$", RegexOptions.RightToLeft);
        if (match.Success && !string.IsNullOrWhiteSpace(match.Groups["remaining"].Value))
            return match.Groups["remaining"].Value.Trim();
        return null;
    }

    private void FormatTable(DocumentLinesEditBuffer lines, IHasRows hasRows, GherkinFormatSettings formatSettings,
        int indentLevel)
    {
        var indent = GetIndent(formatSettings, indentLevel);
        FormatTable(lines, hasRows, formatSettings, indent);
    }

    public void FormatTable(DocumentLinesEditBuffer lines, IHasRows hasRows, GherkinFormatSettings formatSettings,
        string indent, int[]? widths = null)
    {
        widths ??= GetTableWidths(hasRows);
        foreach (var row in hasRows.Rows)
        {
            var result = new StringBuilder();
            result.Append(indent);
            result.Append('|');
            foreach (var item in row.Cells.Select((c, i) => new { c, i }))
            {
                result.Append(formatSettings.TableCellPadding);
                var escapedCellValue = EscapeTableCellValue(item.c.Value);
                var width = widths[item.i];
                var paddedCell = IsTableCellContentRightAligned(item.c.Value, formatSettings)
                    ? escapedCellValue.PadLeft(width)
                    : escapedCellValue.PadRight(width);
                result.Append(paddedCell);
                result.Append(formatSettings.TableCellPadding);
                result.Append('|');
            }

            var unfinishedCell = GetUnfinishedTableCell(lines.GetLineOneBased(row.Location.Line));
            if (unfinishedCell != null)
            {
                result.Append(formatSettings.TableCellPadding);
                var cellIndex = row.Cells.Count();
                if (cellIndex < widths.Length)
                    result.Append(unfinishedCell.PadRight(widths[cellIndex]));
                else
                    result.Append(unfinishedCell);
                result.Append(formatSettings.TableCellPadding);
                result.Append('|');
            }

            SetLine(lines, row, result.ToString());
        }
    }

    private void FormatDocString(DocumentLinesEditBuffer lines, DocString docString,
        GherkinFormatSettings formatSettings, int indentLevel)
    {
        var indent = GetIndent(formatSettings, indentLevel);
        var docStringStartLine = docString.Location.Line;
        var docStringContentLines = DeveroomTagParser.NewLineRe.Split(docString.Content);
        if (string.IsNullOrEmpty(docString.Content) &&
            !string.IsNullOrWhiteSpace(lines.GetLineOneBased(docStringStartLine + 1)))
            docStringContentLines = Array.Empty<string>();

        var docStringEndLine = docStringStartLine + docStringContentLines.Length + 1;
        var delimiterLine = $"{indent}{docString.Delimiter}";

        lines.SetLineOneBased(docStringStartLine, delimiterLine);
        var docStringRow = 1;
        foreach (var contentLine in docStringContentLines)
            lines.SetLineOneBased(docStringStartLine + docStringRow++, $"{indent}{contentLine}");

        lines.SetLineOneBased(docStringEndLine, delimiterLine);
    }

    private static string GetHasDescriptionLine(IHasDescription hasDescription, string indent)
    {
        var line = $"{indent}{hasDescription.Keyword}:";
        if (!string.IsNullOrEmpty(hasDescription.Name))
            line += $" {hasDescription.Name}";
        return line;
    }

    private static void SetTags(DocumentLinesEditBuffer lines, IEnumerable<Tag> tags, string indent)
    {
        var tagGroup = tags.GroupBy(t => t.Location.Line);
        foreach (var tag in tagGroup)
        {
            var line = indent + string.Join(" ", tag.Select(t => t.Name));
            lines.SetLineOneBased(tag.Key, line);
        }
    }

    private static void SetLine(DocumentLinesEditBuffer lines, IHasLocation hasLocation, string line)
    {
        if (hasLocation?.Location != null && hasLocation.Location.Line >= 1
                                          && hasLocation.Location.Column - 1 < line.Length)
            lines.SetLineOneBased(hasLocation.Location.Line, line);
    }

    private static string GetIndent(GherkinFormatSettings formatSettings, int indentLevel)
    {
        if (indentLevel == 0)
            return string.Empty;
        if (indentLevel == 1)
            return formatSettings.Indent;
        return string.Concat(Enumerable.Range(0, indentLevel).Select(_ => formatSettings.Indent));
    }
}
