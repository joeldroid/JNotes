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
            Tags = ["personal", "shopping: weekly"],
            CreatedAt = new DateTimeOffset(2026, 9, 12, 10, 15, 0, TimeSpan.Zero),
            UpdatedAt = new DateTimeOffset(2026, 9, 12, 11, 2, 0, TimeSpan.Zero),
            Body = "- [ ] buy milk\n- [x] buy eggs\n\nsome trailing prose with a colon: like this",
        };

        var fileContent = _serializer.Serialize(note);
        var result = _serializer.Deserialize(fileContent);

        Assert.Equal(note.Id, result.Id);
        Assert.Equal(note.Title, result.Title);
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

    [Theory]
    [InlineData("type: checklist\n")]
    [InlineData("type: spreadsheet\n")]
    [InlineData("type: text\n")]
    [InlineData("")]
    public void Deserialize_ignores_the_type_value(string typeLine)
    {
        var content = $"---\nid: {Guid.NewGuid()}\ntitle: Any type\n{typeLine}tags: []\ncreated: 2026-01-01T00:00:00Z\nupdated: 2026-01-01T00:00:00Z\n---\n- [ ] body";

        var note = _serializer.Deserialize(content);

        Assert.Equal("Any type", note.Title);
        Assert.Equal("- [ ] body", note.Body);
    }

    [Fact]
    public void Serialize_always_writes_type_text()
    {
        var note = new Note
        {
            Id = Guid.NewGuid(),
            Title = "T",
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
            Body = "b",
        };

        Assert.Contains("\ntype: text\n", _serializer.Serialize(note));
    }

    [Fact]
    public void Serialize_of_a_canonical_file_is_byte_identical()
    {
        // The auto-tagger rewrites a note only if Serialize(Deserialize(raw)) == raw, so the front matter's
        // key order and the `type: text` line must stay exactly as existing files have them.
        var raw = "---\n" +
                  "id: 3fa85f64-5717-4562-b3fc-2c963f66afa6\n" +
                  "title: Grocery List\n" +
                  "type: text\n" +
                  "tags:\n" +
                  "- personal\n" +
                  "- shopping\n" +
                  "created: 2026-09-12T10:15:00.0000000Z\n" +
                  "updated: 2026-09-12T11:02:00.0000000Z\n" +
                  "---\n" +
                  "- [ ] buy milk\nsome prose";

        Assert.Equal(raw, _serializer.Serialize(_serializer.Deserialize(raw)));
    }
}
