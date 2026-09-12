using JNotes.Core.Models;
using JNotes.Core.Serialization;

namespace JNotes.Tests.Serialization;

public class NoteMarkdownSerializerTests
{
    private readonly NoteMarkdownSerializer _serializer = new();

    [Fact]
    public void Serialize_then_Deserialize_round_trips_all_fields()
    {
        var note = new Note
        {
            Id = Guid.NewGuid(),
            Title = "Grocery List: things & \"stuff\"",
            Type = NoteType.Checklist,
            Tags = ["personal", "shopping: weekly"],
            CreatedAt = new DateTimeOffset(2026, 9, 12, 10, 15, 0, TimeSpan.Zero),
            UpdatedAt = new DateTimeOffset(2026, 9, 12, 11, 2, 0, TimeSpan.Zero),
            Body = "- [ ] buy milk\n- [x] buy eggs\n\nsome trailing prose with a colon: like this",
        };

        var fileContent = _serializer.Serialize(note);
        var result = _serializer.Deserialize(fileContent);

        Assert.Equal(note.Id, result.Id);
        Assert.Equal(note.Title, result.Title);
        Assert.Equal(note.Type, result.Type);
        Assert.Equal(note.Tags, result.Tags);
        Assert.Equal(note.CreatedAt, result.CreatedAt);
        Assert.Equal(note.UpdatedAt, result.UpdatedAt);
        Assert.Equal(note.Body, result.Body);
    }

    [Fact]
    public void Serialize_then_Deserialize_round_trips_empty_tags_and_multiline_body()
    {
        // Front matter timestamps round-trip at whole-second precision, so seed with a truncated value.
        var now = DateTimeOffset.UtcNow;
        var truncatedNow = new DateTimeOffset(now.Year, now.Month, now.Day, now.Hour, now.Minute, now.Second, TimeSpan.Zero);

        var note = new Note
        {
            Id = Guid.NewGuid(),
            Title = "Plain note",
            Type = NoteType.Text,
            Tags = [],
            CreatedAt = truncatedNow.AddDays(-1),
            UpdatedAt = truncatedNow,
            Body = "line one\nline two\n\nline four after a blank line",
        };

        var fileContent = _serializer.Serialize(note);
        var result = _serializer.Deserialize(fileContent);

        Assert.Empty(result.Tags);
        Assert.Equal(note.Body, result.Body);
        Assert.Equal(note.CreatedAt, result.CreatedAt);
        Assert.Equal(note.UpdatedAt, result.UpdatedAt);
    }

    [Fact]
    public void Deserialize_throws_NoteParseException_when_front_matter_missing()
    {
        var ex = Assert.Throws<NoteParseException>(() => _serializer.Deserialize("just a body, no front matter"));
        Assert.Contains("front matter", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Deserialize_throws_NoteParseException_when_front_matter_unclosed()
    {
        Assert.Throws<NoteParseException>(() => _serializer.Deserialize("---\nid: " + Guid.NewGuid() + "\ntitle: x\n"));
    }

    [Fact]
    public void Deserialize_throws_NoteParseException_for_unknown_note_type()
    {
        var content = $"""
            ---
            id: {Guid.NewGuid()}
            title: Bad type
            type: spreadsheet
            tags: []
            created: 2026-01-01T00:00:00Z
            updated: 2026-01-01T00:00:00Z
            ---
            body
            """;

        Assert.Throws<NoteParseException>(() => _serializer.Deserialize(content));
    }
}
