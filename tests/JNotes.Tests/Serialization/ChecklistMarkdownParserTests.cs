using JNotes.Core.Serialization;

namespace JNotes.Tests.Serialization;

public class ChecklistMarkdownParserTests
{
    private const string MixedBody = """
        - [ ] buy milk
        Just a plain prose line, not a checklist item.
        - [x] buy eggs
        - [X] uppercase mark also counts as checked
        """;

    [Fact]
    public void Parse_finds_only_task_list_lines_with_correct_indices_text_and_state()
    {
        var items = ChecklistMarkdownParser.Parse(MixedBody);

        Assert.Equal(3, items.Count);

        Assert.Equal(0, items[0].LineIndex);
        Assert.Equal("buy milk", items[0].Text);
        Assert.False(items[0].IsChecked);

        Assert.Equal(2, items[1].LineIndex);
        Assert.Equal("buy eggs", items[1].Text);
        Assert.True(items[1].IsChecked);

        Assert.Equal(3, items[2].LineIndex);
        Assert.True(items[2].IsChecked);
    }

    [Fact]
    public void Parse_returns_empty_for_body_with_no_task_list_lines()
    {
        Assert.Empty(ChecklistMarkdownParser.Parse("just some prose\nwith multiple lines"));
    }

    [Fact]
    public void ToggleItem_flips_only_the_targeted_lines_bracket_leaving_everything_else_byte_identical()
    {
        var toggled = ChecklistMarkdownParser.ToggleItem(MixedBody, lineIndex: 0);

        var expectedLines = MixedBody.Replace("\r\n", "\n").Split('\n');
        expectedLines[0] = "- [x] buy milk";
        var expected = string.Join('\n', expectedLines);

        Assert.Equal(expected, toggled);
    }

    [Fact]
    public void ToggleItem_twice_returns_to_the_original_body()
    {
        var toggledOnce = ChecklistMarkdownParser.ToggleItem(MixedBody, lineIndex: 2);
        var toggledTwice = ChecklistMarkdownParser.ToggleItem(toggledOnce, lineIndex: 2);

        Assert.Equal(MixedBody, toggledTwice);
    }

    [Fact]
    public void ToggleItem_preserves_CRLF_line_endings_when_present()
    {
        var crlfBody = "- [ ] one\r\n- [ ] two";

        var toggled = ChecklistMarkdownParser.ToggleItem(crlfBody, lineIndex: 1);

        Assert.Equal("- [ ] one\r\n- [x] two", toggled);
    }

    [Fact]
    public void ToggleItem_throws_when_target_line_is_not_a_checklist_item()
    {
        Assert.Throws<InvalidOperationException>(() => ChecklistMarkdownParser.ToggleItem(MixedBody, lineIndex: 1));
    }

    [Fact]
    public void ToggleItem_throws_when_line_index_is_out_of_range()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => ChecklistMarkdownParser.ToggleItem(MixedBody, lineIndex: 99));
    }
}
