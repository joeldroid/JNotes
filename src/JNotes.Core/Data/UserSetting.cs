namespace JNotes.Core.Data;

/// <summary>
/// One per-user preference row (e.g. "Theme"). A separate table from the global <see cref="SettingEntry"/>
/// key-value store — that one holds installation-wide settings (notes folder path override, etc.)
/// that have nothing to do with any particular user.
/// </summary>
public sealed class UserSetting
{
    public required Guid UserId { get; set; }

    public required string Key { get; set; }

    public required string Value { get; set; }
}
