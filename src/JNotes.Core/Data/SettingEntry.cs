namespace JNotes.Core.Data;

/// <summary>
/// One app setting as a key-value row. A key-value table (rather than a single settings row with
/// one column per setting) is forward-compatible: a new setting is a new row, not a migration.
/// </summary>
public sealed class SettingEntry
{
    public required string Key { get; set; }

    public required string Value { get; set; }
}
