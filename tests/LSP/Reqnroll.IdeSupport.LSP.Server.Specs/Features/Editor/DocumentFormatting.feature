Feature: Document Auto-formatting (F11)

Formats a .feature file via textDocument/formatting and textDocument/rangeFormatting,
fixing indentation, normalising tag whitespace, replacing repeated step keywords
with "And", and aligning data-table cells (design doc F11).

Background:
    Given the LSP server is started

# ── Full document formatting ─────────────────────────────────────────────────

Scenario: Misindented steps are fixed on format document
    When the feature file "Indented.feature" is opened with
        """
        Feature: Addition
        Scenario: Add
        Given I have 50
        When I add
        Then result is 50
        """
    And the document "Indented.feature" is formatted
    Then formatting edits are returned
    And the formatted text contains "    Given I have 50"
    And the formatted text contains "    When I add"
    And the formatted text contains "    Then result is 50"

Scenario: Repeated step keywords are replaced with And
    When the feature file "Keywords.feature" is opened with
        """
        Feature: Keywords
        Scenario: Steps
            Given first step
            Given second step
            When first action
            When second action
            Then first check
            Then second check
        """
    And the document "Keywords.feature" is formatted
    Then formatting edits are returned
    And the formatted text contains "    And second step"
    And the formatted text contains "    And second action"
    And the formatted text contains "    And second check"

Scenario: Data table cells are column-aligned
    When the feature file "Table.feature" is opened with
        """
        Feature: Table
        Scenario: Aligned
            Given a table
            | short | a very long header |
            | x | y |
        """
    And the document "Table.feature" is formatted
    Then formatting edits are returned
    And the formatted text contains "| short | a very long header |"
    And the formatted text contains "| x     | y                  |"

Scenario: Tag whitespace is normalised on format
    When the feature file "Tags.feature" is opened with
        """
          @tag1    @tag2
        Feature: Tagged
          @tag3
        Scenario: Sc
            Given step
        """
    And the document "Tags.feature" is formatted
    Then formatting edits are returned
    And the formatted text contains "@tag1 @tag2"

# ── Range formatting ─────────────────────────────────────────────────────────

Scenario: Range formatting returns edits for the specified range
    When the feature file "Range.feature" is opened with
        """
        Feature: Range
        Scenario: Range
        Given step one
        When step two
        Then step three
        """
    And range formatting is requested for "Range.feature" from line 2 to line 4
    Then formatting edits are returned

# ── Non-feature file ignored ─────────────────────────────────────────────────

Scenario: Non-feature file returns no formatting edits
    Given the file "readme.txt" is open with content
        """
        Some plain text
        """
    When the document "readme.txt" is formatted
    Then no formatting edits are returned

# ── On-type formatting (F12) ─────────────────────────────────────────────────

Scenario: On-type formatting aligns table columns when pipe is typed
    When the feature file "OnTypePipe.feature" is opened with
        """
        Feature: Table
        Scenario: OnType
            Given a table
            | short | a very long header |
            | x | y |
        """
    And on-type formatting is requested for "OnTypePipe.feature" at line 4 column 7 with trigger "|"
    Then formatting edits are returned
    And the formatted text contains "| x     | y                  |"

Scenario: On-type formatting aligns table columns when newline is typed after table row
    When the feature file "OnTypeNewline.feature" is opened with
        """
        Feature: Table
        Scenario: OnType
            Given a table
            | col1 | col2 |
            | short | longer value |

        """
    And on-type formatting is requested for "OnTypeNewline.feature" at line 5 column 0 with trigger "\n"
    Then formatting edits are returned
    And the formatted text contains "| col1  | col2         |"
    And the formatted text contains "| short | longer value |"

Scenario: On-type formatting returns no edits when cursor is not inside a table
    When the feature file "OnTypeStep.feature" is opened with
        """
        Feature: Step
        Scenario: NoTable
            Given a step without table
        """
    And on-type formatting is requested for "OnTypeStep.feature" at line 2 column 20 with trigger "|"
    Then no formatting edits are returned

Scenario: On-type formatting aligns table columns when tab is typed
    When the feature file "OnTypeTab.feature" is opened with
        """
        Feature: Table
        Scenario: OnType
            Given a table
            | short | a very long header |
            | x | y |
        """
    And on-type formatting is requested for "OnTypeTab.feature" at line 4 column 7 with trigger "\t"
    Then formatting edits are returned
    And the formatted text contains "| x     | y                  |"

Scenario: On-type formatting adds trailing pipe for row missing it
    When the feature file "OnTypeTrailingPipe.feature" is opened with
        """
        Feature: Table
        Scenario: OnType
            Given a table
            | col1  | col2         |
            | short | longer value
        """
    And on-type formatting is requested for "OnTypeTrailingPipe.feature" at line 4 column 7 with trigger "|"
    Then formatting edits are returned
    And the formatted text contains "| short | longer value |"
