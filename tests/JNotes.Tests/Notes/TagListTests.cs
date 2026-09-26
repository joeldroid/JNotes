using JNotes.Core.Notes;

namespace JNotes.Tests.Notes;

public class TagListTests
{
    [Fact]
    public void ParseInput_splits_on_commas_trims_and_drops_blanks()
    {
        Assert.Equal(["a", "b"], TagList.ParseInput(" a, ,b ,"));
    }

    [Fact]
    public void ParseInput_returns_empty_for_null_or_blank()
    {
        Assert.Empty(TagList.ParseInput(null));
        Assert.Empty(TagList.ParseInput("  , ,"));
    }

    [Fact]
    public void AddTags_skips_case_insensitive_duplicates_of_existing_tags()
    {
        var result = TagList.AddTags(["Work", "home"], ["work", "HOME", "new"]);

        Assert.Equal(["Work", "home", "new"], result);
    }

    [Fact]
    public void AddTags_skips_duplicates_within_the_same_batch_and_keeps_the_first_spelling_and_order()
    {
        var result = TagList.AddTags(["z"], ["Beta", "alpha", "beta", "ALPHA"]);

        Assert.Equal(["z", "Beta", "alpha"], result);
    }

    [Fact]
    public void AddTags_parses_comma_separated_input()
    {
        var result = TagList.AddTags([], ["a1, b2"]);

        Assert.Equal(["a1", "b2"], result);
    }

    [Fact]
    public void RemoveTag_removes_by_exact_ordinal_match_only()
    {
        Assert.Equal(["Work", "x"], TagList.RemoveTag(["Work", "work", "x"], "work"));
        Assert.Equal(["Work", "work"], TagList.RemoveTag(["Work", "work"], "WORK"));
    }

    [Fact]
    public void RemoveTag_removes_only_the_first_occurrence()
    {
        Assert.Equal(["a"], TagList.RemoveTag(["a", "a"], "a"));
    }
}
