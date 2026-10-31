namespace JNotes.Core.Serialization;

/// <summary>
/// The YAML-shaped front matter DTO, kept separate from <see cref="Models.Note"/> so the wire
/// format (plain strings/lists, no domain enum) can evolve independently of the domain model.
/// Timestamps are stored as plain UTC <see cref="DateTime"/> rather than <see cref="DateTimeOffset"/>
/// to sidestep YamlDotNet's narrower built-in support for the latter; the mapper to/from
/// <see cref="Models.Note"/> is where the UTC convention is enforced.
/// </summary>
internal sealed class NoteFrontMatter
{
    public Guid Id { get; set; }

    public string Title { get; set; } = string.Empty;

    /// <summary>
    /// Always written as <c>text</c> and ignored on read: kept so existing files stay byte-identical (the
    /// auto-tagger's canonical-format guard) and the format stays stable; legacy <c>checklist</c> files open as
    /// text notes. Must keep its position between <see cref="Title"/> and <see cref="Tags"/>.
    /// </summary>
    public string Type { get; set; } = "text";

    public List<string> Tags { get; set; } = [];

    public DateTime Created { get; set; }

    public DateTime Updated { get; set; }
}
