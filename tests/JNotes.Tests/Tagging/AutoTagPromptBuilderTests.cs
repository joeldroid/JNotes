using JNotes.Core.Models;
using JNotes.Core.Tagging;

namespace JNotes.Tests.Tagging;

public class AutoTagPromptBuilderTests
{
    private static AutoTagPrompt Build(
        string? body,
        NoteType type = NoteType.Text,
        string? title = "Title",
        string[]? current = null,
        string[]? vocabulary = null) =>
        AutoTagPromptBuilder.Build(type, title, body, current ?? [], vocabulary ?? []);

    [Fact]
    public void A_long_body_is_truncated_with_a_marker_and_a_short_one_is_unchanged()
    {
        var longBody = new string('x', AutoTagPromptBuilder.MaxBodyChars + 500);
        var truncated = Build(longBody).UserMessage;

        Assert.Contains(new string('x', AutoTagPromptBuilder.MaxBodyChars) + AutoTagPromptBuilder.TruncationMarker, truncated);
        Assert.DoesNotContain(new string('x', AutoTagPromptBuilder.MaxBodyChars + 1), truncated);

        var shortMessage = Build("short body").UserMessage;
        Assert.Contains("<note>\nshort body\n</note>", shortMessage);
        Assert.DoesNotContain("truncated", shortMessage);
    }

    [Fact]
    public void Image_markdown_is_replaced_by_its_alt_text()
    {
        var message = Build("before ![a diagram](images/0123456789abcdef0123456789abcdef.png) after").UserMessage;

        Assert.Contains("before a diagram after", message);
        Assert.DoesNotContain("images/", message);
    }

    [Fact]
    public void The_vocabulary_is_listed_and_capped()
    {
        var vocabulary = Enumerable.Range(1, AutoTagPromptBuilder.MaxVocabulary + 10).Select(i => $"tag{i}").ToArray();
        var message = Build("b", vocabulary: vocabulary).UserMessage;

        Assert.Contains("tag1,", message);
        Assert.Contains($"tag{AutoTagPromptBuilder.MaxVocabulary}\n", message);
        Assert.DoesNotContain($"tag{AutoTagPromptBuilder.MaxVocabulary + 1}", message);
    }

    [Fact]
    public void An_empty_vocabulary_and_no_current_tags_read_as_none()
    {
        var lines = Build("b").UserMessage.Split('\n');

        Assert.Equal("Existing tags (prefer these when relevant): (none)", lines[0]);
        Assert.Equal("Tags already on this note (do not repeat): (none)", lines[1]);
    }

    [Fact]
    public void Current_tags_are_listed()
    {
        var message = Build("b", current: ["alpha", "beta"]).UserMessage;

        Assert.Contains("Tags already on this note (do not repeat): alpha, beta", message);
    }

    [Theory]
    [InlineData(NoteType.Text, "Note type: text")]
    [InlineData(NoteType.Checklist, "Note type: checklist")]
    public void The_note_type_is_stated(NoteType type, string expected)
    {
        Assert.Contains(expected, Build("b", type).UserMessage);
    }

    [Fact]
    public void The_title_is_truncated_and_null_title_and_body_are_empty()
    {
        Assert.DoesNotContain(new string('t', AutoTagPromptBuilder.MaxTitleChars + 1), Build("b", title: new string('t', 500)).UserMessage);

        var message = Build(null, title: null).UserMessage;
        Assert.Contains("Title: \n", message);
        Assert.Contains("<note>\n\n</note>", message);
    }

    [Fact]
    public void The_body_sits_inside_the_note_tags_and_the_system_message_asks_for_tags_json()
    {
        var prompt = Build("the body");

        Assert.EndsWith("<note>\nthe body\n</note>", prompt.UserMessage);
        Assert.Contains("\"tags\"", prompt.SystemMessage);
    }
}
