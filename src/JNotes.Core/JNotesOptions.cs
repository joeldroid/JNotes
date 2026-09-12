namespace JNotes.Core;

/// <summary>Bound from the "JNotes" configuration section (see <c>appsettings.json</c>/env vars).</summary>
public sealed class JNotesOptions
{
    public const string SectionName = "JNotes";

    /// <summary>Default notes folder used only until a "NotesFolderPath" setting exists in the database.</summary>
    public string NotesFolderPath { get; set; } = "notes";

    public string DbPath { get; set; } = "jnotes.db";
}
