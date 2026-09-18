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

    private const string MultiBlockBody = "## Groceries\n- [ ] milk\n- [ ] eggs\n\n## Hardware\n- [ ] nails";

    private const string NestedBody = "- [ ] a\n  - [ ] a1\n  - [ ] a2\n- [ ] b";

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
    public void SetItemChecked_checking_an_item_moves_its_line_to_the_bottom_of_its_block()
    {
        var result = ChecklistMarkdownParser.SetItemChecked("- [ ] a\n- [ ] b\n- [ ] c", 0, true);

        Assert.Equal("- [ ] b\n- [ ] c\n- [x] a", result);
    }

    [Fact]
    public void SetItemChecked_unchecking_an_item_moves_it_to_the_end_of_the_unchecked_run()
    {
        var result = ChecklistMarkdownParser.SetItemChecked("- [ ] a\n- [ ] b\n- [x] c\n- [x] d", 3, false);

        Assert.Equal("- [ ] a\n- [ ] b\n- [ ] d\n- [x] c", result);
    }

    [Fact]
    public void SetItemChecked_preserves_relative_order_within_each_group()
    {
        var result = ChecklistMarkdownParser.SetItemChecked("- [ ] a\n- [ ] b\n- [x] c\n- [ ] d\n- [x] e", 1, true);

        // Unchecked a, d keep order; checked c, e keep order; the newly checked b goes last.
        Assert.Equal("- [ ] a\n- [ ] d\n- [x] c\n- [x] e\n- [x] b", result);
    }

    [Fact]
    public void SetItemChecked_is_idempotent_when_the_item_is_already_in_the_requested_state()
    {
        var once = ChecklistMarkdownParser.SetItemChecked("- [ ] a\n- [ ] b\n- [ ] c", 0, true);
        var itemNowAtBottom = ChecklistMarkdownParser.Parse(once).Single(i => i.Text == "a").LineIndex;

        var twice = ChecklistMarkdownParser.SetItemChecked(once, itemNowAtBottom, true);

        Assert.Equal(once, twice);
    }

    [Fact]
    public void SetItemChecked_leaves_an_already_ordered_block_untouched_apart_from_the_bracket()
    {
        var result = ChecklistMarkdownParser.SetItemChecked("- [ ] a\n- [ ] b", 1, true);

        Assert.Equal("- [ ] a\n- [x] b", result);
    }

    [Fact]
    public void SetItemChecked_does_not_move_prose_or_blank_lines()
    {
        var result = ChecklistMarkdownParser.SetItemChecked("intro\n- [ ] a\n- [ ] b\n\noutro", 1, true);

        Assert.Equal("intro\n- [ ] b\n- [x] a\n\noutro", result);
    }

    [Fact]
    public void SetItemChecked_never_moves_an_item_out_of_its_own_block()
    {
        var result = ChecklistMarkdownParser.SetItemChecked(MultiBlockBody, 1, true);

        Assert.Equal("## Groceries\n- [ ] eggs\n- [x] milk\n\n## Hardware\n- [ ] nails", result);
    }

    [Fact]
    public void SetItemChecked_treats_a_blank_line_as_a_block_boundary()
    {
        var result = ChecklistMarkdownParser.SetItemChecked("- [ ] a\n\n- [ ] b", 0, true);

        Assert.Equal("- [x] a\n\n- [ ] b", result);
    }

    [Fact]
    public void SetItemChecked_treats_differently_indented_items_as_separate_blocks()
    {
        // Checking the parent: it is a single-item block, so nothing moves.
        var parentChecked = ChecklistMarkdownParser.SetItemChecked(NestedBody, 0, true);
        Assert.Equal("- [x] a\n  - [ ] a1\n  - [ ] a2\n- [ ] b", parentChecked);

        // Checking a child reorders only among its siblings.
        var childChecked = ChecklistMarkdownParser.SetItemChecked(NestedBody, 1, true);
        Assert.Equal("- [ ] a\n  - [ ] a2\n  - [x] a1\n- [ ] b", childChecked);
    }

    [Fact]
    public void SetItemChecked_does_nothing_to_order_for_a_single_item_block()
    {
        var result = ChecklistMarkdownParser.SetItemChecked("intro\n- [ ] only\noutro", 1, true);

        Assert.Equal("intro\n- [x] only\noutro", result);
    }

    [Fact]
    public void SetItemChecked_repairs_an_interleaved_block_into_unchecked_then_checked()
    {
        var result = ChecklistMarkdownParser.SetItemChecked("- [x] a\n- [ ] b\n- [x] c\n- [ ] d", 3, false);

        Assert.Equal("- [ ] b\n- [ ] d\n- [x] a\n- [x] c", result);
    }

    [Fact]
    public void SetItemChecked_preserves_CRLF_line_endings_when_present()
    {
        var result = ChecklistMarkdownParser.SetItemChecked("- [ ] one\r\n- [ ] two", 0, true);

        Assert.Equal("- [ ] two\r\n- [x] one", result);
    }

    [Fact]
    public void SetItemChecked_preserves_uppercase_X_marks_on_other_lines()
    {
        var result = ChecklistMarkdownParser.SetItemChecked("- [ ] a\n- [X] b\n- [ ] c", 0, true);

        Assert.Equal("- [ ] c\n- [X] b\n- [x] a", result);
    }

    [Fact]
    public void SetItemChecked_throws_when_target_line_is_not_a_checklist_item()
    {
        Assert.Throws<InvalidOperationException>(() => ChecklistMarkdownParser.SetItemChecked(MixedBody, 1, true));
    }

    [Fact]
    public void SetItemChecked_throws_when_line_index_is_out_of_range()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => ChecklistMarkdownParser.SetItemChecked(MixedBody, 99, true));
        Assert.Throws<ArgumentOutOfRangeException>(() => ChecklistMarkdownParser.SetItemChecked(MixedBody, -1, true));
    }

    [Fact]
    public void SetItemChecked_then_Parse_reports_line_indices_matching_the_reordered_body()
    {
        var result = ChecklistMarkdownParser.SetItemChecked("- [ ] a\n- [ ] b\n- [ ] c", 0, true);

        var items = ChecklistMarkdownParser.Parse(result);

        Assert.Equal(["b", "c", "a"], items.Select(i => i.Text));
        Assert.Equal([0, 1, 2], items.Select(i => i.LineIndex));
        Assert.Equal([false, false, true], items.Select(i => i.IsChecked));
    }

    [Fact]
    public void AppendItems_adds_a_task_line_to_an_empty_body()
    {
        Assert.Equal("- [ ] a\n", ChecklistMarkdownParser.AppendItems("", "a"));
    }

    [Fact]
    public void AppendItems_appends_after_the_last_unchecked_item_and_above_checked_ones()
    {
        Assert.Equal("- [ ] a\n- [ ] b", ChecklistMarkdownParser.AppendItems("- [ ] a", "b"));
        Assert.Equal("- [ ] a\n- [ ] c\n- [x] b\n", ChecklistMarkdownParser.AppendItems("- [ ] a\n- [x] b\n", "c"));
    }

    [Fact]
    public void AppendItems_appends_to_the_last_block_when_the_body_has_several_blocks()
    {
        var result = ChecklistMarkdownParser.AppendItems(MultiBlockBody, "screws");

        Assert.Equal("## Groceries\n- [ ] milk\n- [ ] eggs\n\n## Hardware\n- [ ] nails\n- [ ] screws", result);
    }

    [Fact]
    public void AppendItems_appends_at_the_end_of_a_body_with_no_checklist_lines()
    {
        Assert.Equal("some prose\n- [ ] a", ChecklistMarkdownParser.AppendItems("some prose", "a"));
    }

    [Fact]
    public void AppendItems_preserves_a_trailing_newline()
    {
        Assert.Equal("some prose\n- [ ] a\n", ChecklistMarkdownParser.AppendItems("some prose\n", "a"));
        Assert.Equal("- [ ] a\n- [ ] b\n", ChecklistMarkdownParser.AppendItems("- [ ] a\n", "b"));
    }

    [Fact]
    public void AppendItems_trims_surrounding_whitespace_from_the_text()
    {
        Assert.Equal("- [ ] a\n- [ ] b", ChecklistMarkdownParser.AppendItems("- [ ] a", "   b \t"));
    }

    [Fact]
    public void AppendItems_creates_one_item_per_non_blank_line_of_multiline_text()
    {
        var result = ChecklistMarkdownParser.AppendItems("- [ ] a", "b\r\n\n  c  \nd");

        Assert.Equal("- [ ] a\n- [ ] b\n- [ ] c\n- [ ] d", result);
    }

    [Fact]
    public void AppendItems_matches_the_indent_of_the_block_it_appends_to()
    {
        var result = ChecklistMarkdownParser.AppendItems(NestedBody, "a3");

        // The last task line is "- [ ] b" (indent ""), so that block gets the new item.
        Assert.Equal("- [ ] a\n  - [ ] a1\n  - [ ] a2\n- [ ] b\n- [ ] a3", result);

        var indented = ChecklistMarkdownParser.AppendItems("- [ ] a\n  - [ ] a1", "a2");
        Assert.Equal("- [ ] a\n  - [ ] a1\n  - [ ] a2", indented);
    }

    [Fact]
    public void AppendItems_preserves_CRLF_line_endings_when_present()
    {
        var result = ChecklistMarkdownParser.AppendItems("- [ ] one\r\n- [x] two", "three\nfour");

        Assert.Equal("- [ ] one\r\n- [ ] three\r\n- [ ] four\r\n- [x] two", result);
    }

    [Theory]
    [InlineData("- [ ] nested")]
    [InlineData("---")]
    [InlineData("# heading")]
    [InlineData("[x]")]
    public void AppendItems_keeps_markdown_looking_text_on_a_single_line(string text)
    {
        var result = ChecklistMarkdownParser.AppendItems("- [ ] first", text);

        var items = ChecklistMarkdownParser.Parse(result);
        Assert.Equal(2, items.Count);
        Assert.Equal(text, items[1].Text);
        Assert.Equal(2, result.Split('\n').Length);
    }

    [Fact]
    public void AppendItems_stores_very_long_text_as_one_line()
    {
        var longText = new string('x', 10_000);

        var result = ChecklistMarkdownParser.AppendItems("- [ ] first", longText);

        var lines = result.Split('\n');
        Assert.Equal(2, lines.Length);
        Assert.Equal($"- [ ] {longText}", lines[1]);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\n\n")]
    [InlineData("\r\n")]
    public void AppendItems_throws_for_text_with_no_non_blank_content(string text)
    {
        Assert.Throws<ArgumentException>(() => ChecklistMarkdownParser.AppendItems("- [ ] a", text));
    }
}
