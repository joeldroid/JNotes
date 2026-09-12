namespace JNotes.Core;

/// <summary>
/// Populated from the flat "NOTES_PATH"/"DB_PATH" configuration keys (see
/// <c>appsettings.json</c>, or the same-named env vars — <see cref="ServiceCollectionExtensions.AddJNotesCore"/>).
/// </summary>
public sealed class JNotesOptions
{
    /// <summary>Default notes folder used only until a "NotesFolderPath" setting exists in the database.</summary>
    public string NotesFolderPath { get; set; } = "notes";

    public string DbPath { get; set; } = "jnotes.db";
}
