using JNotes.Core.Models;

namespace JNotes.Core.Serialization;

/// <summary>
/// Converts between a <see cref="Note"/> and the raw text of its markdown file (front matter + body).
/// Pure in-memory string handling, no disk I/O — see <c>INoteFileStore</c> for reading/writing files.
/// </summary>
public interface INoteMarkdownSerializer
{
    /// <exception cref="NoteParseException">The file's front matter is missing, malformed, or names an unknown note type.</exception>
    Note Deserialize(string fileContent);

    string Serialize(Note note);
}
