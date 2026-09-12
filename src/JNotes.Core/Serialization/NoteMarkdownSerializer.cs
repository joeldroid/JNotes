using JNotes.Core.Models;
using YamlDotNet.Core;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace JNotes.Core.Serialization;

/// <inheritdoc cref="INoteMarkdownSerializer"/>
public sealed class NoteMarkdownSerializer : INoteMarkdownSerializer
{
    private readonly IDeserializer _yamlDeserializer = new DeserializerBuilder()
        .WithNamingConvention(LowerCaseNamingConvention.Instance)
        .IgnoreUnmatchedProperties()
        .Build();

    private readonly ISerializer _yamlSerializer = new SerializerBuilder()
        .WithNamingConvention(LowerCaseNamingConvention.Instance)
        .Build();

    public Note Deserialize(string fileContent)
    {
        var (yamlText, body) = FrontMatterSplitter.Split(fileContent);

        NoteFrontMatter? frontMatter;
        try
        {
            frontMatter = _yamlDeserializer.Deserialize<NoteFrontMatter>(yamlText);
        }
        catch (YamlException ex)
        {
            throw new NoteParseException($"Failed to parse note front matter: {ex.Message}", ex);
        }

        if (frontMatter is null)
        {
            throw new NoteParseException("Note front matter is empty.");
        }

        if (!Enum.TryParse<NoteType>(frontMatter.Type, ignoreCase: true, out var noteType))
        {
            throw new NoteParseException($"Unknown note type '{frontMatter.Type}' in front matter.");
        }

        return new Note
        {
            Id = frontMatter.Id,
            Title = frontMatter.Title,
            Type = noteType,
            Tags = frontMatter.Tags.AsReadOnly(),
            CreatedAt = new DateTimeOffset(DateTime.SpecifyKind(frontMatter.Created, DateTimeKind.Utc)),
            UpdatedAt = new DateTimeOffset(DateTime.SpecifyKind(frontMatter.Updated, DateTimeKind.Utc)),
            Body = body,
        };
    }

    public string Serialize(Note note)
    {
        var frontMatter = new NoteFrontMatter
        {
            Id = note.Id,
            Title = note.Title,
            Type = note.Type.ToString().ToLowerInvariant(),
            Tags = note.Tags.ToList(),
            Created = note.CreatedAt.UtcDateTime,
            Updated = note.UpdatedAt.UtcDateTime,
        };

        var yamlText = _yamlSerializer.Serialize(frontMatter);
        return FrontMatterSplitter.Join(yamlText, note.Body);
    }
}
