using JNotes.Core.Tagging;

namespace JNotes.Tests.Tagging;

public class AutoTagNormalizerTests
{
    private static IReadOnlyList<string> Normalize(string[] raw, string[]? current = null, string[]? vocabulary = null) =>
        AutoTagNormalizer.Normalize(raw, current ?? [], vocabulary ?? []);

    [Theory]
    [InlineData("  #AI  ", "ai")]
    [InlineData("Machine Learning", "machine-learning")]
    [InlineData("a,b;c", "abc")]
    [InlineData("\"quoted\" [tag] (x) {y} <z> 'q' `b`", "quoted-tag-x-y-z-q-b")]
    [InlineData("--weird--", "weird")]
    [InlineData("multi   space", "multi-space")]
    [InlineData("a--b", "a-b")]
    [InlineData("/path/", "path")]
    public void NormalizeOne_cleans_up_a_tag(string raw, string expected)
    {
        Assert.Equal(expected, AutoTagNormalizer.NormalizeOne(raw));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("#")]
    [InlineData("note")]
    [InlineData("Checklist")]
    [InlineData("TODO")]
    [InlineData("to-do")]
    public void NormalizeOne_drops_empty_and_generic_tags(string raw)
    {
        Assert.Null(AutoTagNormalizer.NormalizeOne(raw));
    }

    [Fact]
    public void NormalizeOne_drops_a_tag_longer_than_the_limit_but_keeps_one_at_it()
    {
        Assert.Null(AutoTagNormalizer.NormalizeOne(new string('a', AutoTagNormalizer.MaxTagLength + 1)));
        Assert.NotNull(AutoTagNormalizer.NormalizeOne(new string('a', AutoTagNormalizer.MaxTagLength)));
    }

    [Fact]
    public void Dedupes_case_insensitively_and_by_key()
    {
        Assert.Equal(["work-items", "ai"], Normalize(["Work Items", "work-items", "work_items", "AI", "ai"]));
    }

    [Fact]
    public void Excludes_tags_already_on_the_note_by_key()
    {
        Assert.Equal(["ai"], Normalize(["work", "AI"], current: ["Work"]));
    }

    [Fact]
    public void Uses_the_vocabulary_spelling_when_a_tag_matches()
    {
        Assert.Equal(["Home Lab", "servers"], Normalize(["home-lab", "servers"], vocabulary: ["Home Lab"]));
    }

    [Fact]
    public void Caps_at_five_keeping_the_model_order()
    {
        Assert.Equal(
            ["a", "b", "c", "d", "e"],
            Normalize(["a", "b", "c", "d", "e", "f", "g"]));
    }

    [Fact]
    public void Dropped_tags_dont_count_towards_the_cap()
    {
        Assert.Equal(
            ["a", "b", "c", "d", "e"],
            Normalize(["note", "a", "todo", "b", "c", "d", "e", "f"]));
    }

    [Fact]
    public void Empty_input_gives_an_empty_list()
    {
        Assert.Empty(Normalize([]));
    }
}
