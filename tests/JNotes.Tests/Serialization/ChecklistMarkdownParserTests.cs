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

    // The uncategorized section: the whole body when it has no headings, which is what most of these cover.
    private static string AppendItems(string body, string text) =>
        ChecklistMarkdownParser.AppendItemsToCategory(body, text, null);

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
    public void AppendItemsToCategory_adds_a_task_line_to_an_empty_body()
    {
        Assert.Equal("- [ ] a\n", AppendItems("", "a"));
    }

    [Fact]
    public void AppendItemsToCategory_appends_after_the_last_unchecked_item_and_above_checked_ones()
    {
        Assert.Equal("- [ ] a\n- [ ] b", AppendItems("- [ ] a", "b"));
        Assert.Equal("- [ ] a\n- [ ] c\n- [x] b\n", AppendItems("- [ ] a\n- [x] b\n", "c"));
    }

    [Fact]
    public void AppendItemsToCategory_appends_to_the_targeted_block_when_the_body_has_several_blocks()
    {
        var result = ChecklistMarkdownParser.AppendItemsToCategory(MultiBlockBody, "screws", 4);

        Assert.Equal("## Groceries\n- [ ] milk\n- [ ] eggs\n\n## Hardware\n- [ ] nails\n- [ ] screws", result);
    }

    [Fact]
    public void AppendItemsToCategory_appends_at_the_end_of_a_body_with_no_checklist_lines()
    {
        Assert.Equal("some prose\n- [ ] a", AppendItems("some prose", "a"));
    }

    [Fact]
    public void AppendItemsToCategory_preserves_a_trailing_newline()
    {
        Assert.Equal("some prose\n- [ ] a\n", AppendItems("some prose\n", "a"));
        Assert.Equal("- [ ] a\n- [ ] b\n", AppendItems("- [ ] a\n", "b"));
    }

    [Fact]
    public void AppendItemsToCategory_trims_surrounding_whitespace_from_the_text()
    {
        Assert.Equal("- [ ] a\n- [ ] b", AppendItems("- [ ] a", "   b \t"));
    }

    [Fact]
    public void AppendItemsToCategory_creates_one_item_per_non_blank_line_of_multiline_text()
    {
        var result = AppendItems("- [ ] a", "b\r\n\n  c  \nd");

        Assert.Equal("- [ ] a\n- [ ] b\n- [ ] c\n- [ ] d", result);
    }

    [Fact]
    public void AppendItemsToCategory_matches_the_indent_of_the_block_it_appends_to()
    {
        var result = AppendItems(NestedBody, "a3");

        // The last task line is "- [ ] b" (indent ""), so that block gets the new item.
        Assert.Equal("- [ ] a\n  - [ ] a1\n  - [ ] a2\n- [ ] b\n- [ ] a3", result);

        var indented = AppendItems("- [ ] a\n  - [ ] a1", "a2");
        Assert.Equal("- [ ] a\n  - [ ] a1\n  - [ ] a2", indented);
    }

    [Fact]
    public void AppendItemsToCategory_preserves_CRLF_line_endings_when_present()
    {
        var result = AppendItems("- [ ] one\r\n- [x] two", "three\nfour");

        Assert.Equal("- [ ] one\r\n- [ ] three\r\n- [ ] four\r\n- [x] two", result);
    }

    [Theory]
    [InlineData("- [ ] nested")]
    [InlineData("---")]
    [InlineData("# heading")]
    [InlineData("[x]")]
    public void AppendItemsToCategory_keeps_markdown_looking_text_on_a_single_line(string text)
    {
        var result = AppendItems("- [ ] first", text);

        var items = ChecklistMarkdownParser.Parse(result);
        Assert.Equal(2, items.Count);
        Assert.Equal(text, items[1].Text);
        Assert.Equal(2, result.Split('\n').Length);
    }

    [Fact]
    public void AppendItemsToCategory_stores_very_long_text_as_one_line()
    {
        var longText = new string('x', 10_000);

        var result = AppendItems("- [ ] first", longText);

        var lines = result.Split('\n');
        Assert.Equal(2, lines.Length);
        Assert.Equal($"- [ ] {longText}", lines[1]);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\n\n")]
    [InlineData("\r\n")]
    public void AppendItemsToCategory_throws_for_text_with_no_non_blank_content(string text)
    {
        Assert.Throws<ArgumentException>(() => AppendItems("- [ ] a", text));
    }

    [Fact]
    public void ParseSections_returns_one_uncategorized_section_when_there_are_no_headings()
    {
        var sections = ChecklistMarkdownParser.ParseSections("- [ ] a\n- [x] b");

        var section = Assert.Single(sections);
        Assert.Null(section.HeadingLineIndex);
        Assert.Null(section.Title);
        Assert.Equal(2, section.Items.Count);
    }

    [Fact]
    public void ParseSections_returns_one_empty_uncategorized_section_for_an_empty_body()
    {
        var section = Assert.Single(ChecklistMarkdownParser.ParseSections(""));

        Assert.Null(section.Title);
        Assert.Empty(section.Items);
    }

    [Fact]
    public void ParseSections_groups_items_under_their_headings_with_line_indices()
    {
        var sections = ChecklistMarkdownParser.ParseSections(MultiBlockBody);

        Assert.Equal(2, sections.Count);
        Assert.Equal(0, sections[0].HeadingLineIndex);
        Assert.Equal("Groceries", sections[0].Title);
        Assert.Equal(["milk", "eggs"], sections[0].Items.Select(i => i.Text));
        Assert.Equal(4, sections[1].HeadingLineIndex);
        Assert.Equal("Hardware", sections[1].Title);
        Assert.Equal(5, sections[1].Items.Single().LineIndex);
    }

    [Fact]
    public void ParseSections_keeps_items_before_the_first_heading_as_an_uncategorized_section()
    {
        var sections = ChecklistMarkdownParser.ParseSections("- [ ] loose\n## Later\n- [ ] a");

        Assert.Equal(2, sections.Count);
        Assert.Null(sections[0].Title);
        Assert.Equal("loose", sections[0].Items.Single().Text);
        Assert.Equal("Later", sections[1].Title);
    }

    [Fact]
    public void ParseSections_omits_an_empty_uncategorized_section_when_headings_exist()
    {
        var sections = ChecklistMarkdownParser.ParseSections("## Only\n- [ ] a");

        Assert.Equal("Only", Assert.Single(sections).Title);
    }

    [Fact]
    public void ParseSections_keeps_a_category_with_no_items()
    {
        var sections = ChecklistMarkdownParser.ParseSections("## Empty\n\n## Full\n- [ ] a");

        Assert.Equal(2, sections.Count);
        Assert.Empty(sections[0].Items);
        Assert.Single(sections[1].Items);
    }

    [Theory]
    [InlineData("# One", "One")]
    [InlineData("###### Six", "Six")]
    [InlineData("##   Padded   ", "Padded")]
    public void ParseSections_treats_atx_headings_of_any_level_as_categories(string heading, string title)
    {
        var sections = ChecklistMarkdownParser.ParseSections($"{heading}\n- [ ] a");

        Assert.Equal(title, Assert.Single(sections).Title);
    }

    [Theory]
    [InlineData("#tag")]
    [InlineData("####### seven")]
    [InlineData("#")]
    public void ParseSections_does_not_treat_non_headings_as_categories(string line)
    {
        var section = Assert.Single(ChecklistMarkdownParser.ParseSections($"{line}\n- [ ] a"));

        Assert.Null(section.Title);
    }

    [Fact]
    public void AppendItemsToCategory_adds_under_the_chosen_category_above_its_checked_items()
    {
        var body = "## A\n- [ ] a1\n- [x] a2\n\n## B\n- [ ] b1";

        var result = ChecklistMarkdownParser.AppendItemsToCategory(body, "new", 0);

        Assert.Equal("## A\n- [ ] a1\n- [ ] new\n- [x] a2\n\n## B\n- [ ] b1", result);
    }

    [Fact]
    public void AppendItemsToCategory_leaves_other_categories_untouched()
    {
        var body = "## A\n- [ ] a1\n\n## B\n- [ ] b1";

        var result = ChecklistMarkdownParser.AppendItemsToCategory(body, "b2", 3);

        Assert.Equal("## A\n- [ ] a1\n\n## B\n- [ ] b1\n- [ ] b2", result);
    }

    [Fact]
    public void AppendItemsToCategory_puts_items_directly_under_an_empty_categorys_heading()
    {
        var result = ChecklistMarkdownParser.AppendItemsToCategory("## A\n\n## B\n- [ ] b1", "a1", 0);

        Assert.Equal("## A\n- [ ] a1\n\n## B\n- [ ] b1", result);
    }

    [Fact]
    public void AppendItemsToCategory_adds_to_a_trailing_empty_category_and_keeps_the_trailing_newline()
    {
        Assert.Equal("## A\n- [ ] a1", ChecklistMarkdownParser.AppendItemsToCategory("## A", "a1", 0));
        Assert.Equal("## A\n- [ ] a1\n", ChecklistMarkdownParser.AppendItemsToCategory("## A\n", "a1", 0));
    }

    [Fact]
    public void AppendItemsToCategory_with_null_targets_the_items_before_the_first_heading()
    {
        var result = ChecklistMarkdownParser.AppendItemsToCategory("- [ ] loose\n\n## A\n- [ ] a1", "more", null);

        Assert.Equal("- [ ] loose\n- [ ] more\n\n## A\n- [ ] a1", result);
    }

    [Fact]
    public void AppendItemsToCategory_with_null_and_a_heading_on_the_first_line_inserts_nothing_above_it()
    {
        // The uncategorized range is empty here; the UI never offers it, but the API must not throw.
        var result = ChecklistMarkdownParser.AppendItemsToCategory("## A\n- [ ] a1", "x", null);

        Assert.Equal("- [ ] x\n## A\n- [ ] a1", result);
    }

    [Fact]
    public void AppendItemsToCategory_adds_one_item_per_line_in_order()
    {
        var result = ChecklistMarkdownParser.AppendItemsToCategory("## A\n- [ ] a1\n\n## B", "x\ny", 0);

        Assert.Equal("## A\n- [ ] a1\n- [ ] x\n- [ ] y\n\n## B", result);
    }

    [Fact]
    public void AppendItemsToCategory_preserves_CRLF_line_endings()
    {
        var result = ChecklistMarkdownParser.AppendItemsToCategory("## A\r\n- [ ] a1\r\n\r\n## B", "x", 0);

        Assert.Equal("## A\r\n- [ ] a1\r\n- [ ] x\r\n\r\n## B", result);
    }

    [Fact]
    public void AppendItemsToCategory_throws_when_the_target_line_is_not_a_heading()
    {
        Assert.Throws<InvalidOperationException>(() => ChecklistMarkdownParser.AppendItemsToCategory("## A\n- [ ] a1", "x", 1));
    }

    [Fact]
    public void AppendItemsToCategory_throws_when_the_heading_index_is_out_of_range()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => ChecklistMarkdownParser.AppendItemsToCategory("## A", "x", 5));
    }

    [Fact]
    public void AppendItemsToCategory_throws_for_blank_text()
    {
        Assert.Throws<ArgumentException>(() => ChecklistMarkdownParser.AppendItemsToCategory("## A", "  ", 0));
    }

    [Fact]
    public void AppendItemsToCategory_then_SetItemChecked_keeps_checked_items_inside_their_category()
    {
        var body = ChecklistMarkdownParser.AppendItemsToCategory("## A\n- [ ] a1\n\n## B\n- [ ] b1", "a2", 0);

        var result = ChecklistMarkdownParser.SetItemChecked(body, 1, true);

        Assert.Equal("## A\n- [ ] a2\n- [x] a1\n\n## B\n- [ ] b1", result);
    }

    [Theory]
    [InlineData("", "Home", "## Home\n")]
    [InlineData("- [ ] a", "Home", "- [ ] a\n\n## Home")]
    [InlineData("- [ ] a\n", "Home", "- [ ] a\n\n## Home\n")]
    [InlineData("- [ ] a\n\n\n", "Home", "- [ ] a\n\n## Home\n\n\n")]
    [InlineData("- [ ] a", "  Home  ", "- [ ] a\n\n## Home")]
    public void AddCategory_appends_a_heading_after_the_last_content(string body, string name, string expected)
    {
        Assert.Equal(expected, ChecklistMarkdownParser.AddCategory(body, name));
    }

    [Fact]
    public void AddCategory_flattens_line_breaks_in_the_name_into_one_heading_line()
    {
        Assert.Equal("## Home Garden", ChecklistMarkdownParser.AddCategory("", "Home\r\nGarden").TrimEnd());
    }

    [Fact]
    public void AddCategory_preserves_CRLF_line_endings()
    {
        Assert.Equal("- [ ] a\r\n\r\n## Home\r\n", ChecklistMarkdownParser.AddCategory("- [ ] a\r\n", "Home"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\r\n")]
    public void AddCategory_throws_for_a_blank_name(string name)
    {
        Assert.Throws<ArgumentException>(() => ChecklistMarkdownParser.AddCategory("- [ ] a", name));
    }

    [Fact]
    public void AddCategory_makes_a_new_empty_category_that_ParseSections_reports()
    {
        var sections = ChecklistMarkdownParser.ParseSections(ChecklistMarkdownParser.AddCategory("- [ ] a", "Home"));

        Assert.Equal(2, sections.Count);
        Assert.Null(sections[0].Title);
        Assert.Equal("Home", sections[1].Title);
        Assert.Empty(sections[1].Items);
    }
}
