using JNotes.Core.Users;

namespace JNotes.Tests.Users;

public class UserInitialsTests
{
    [Fact]
    public void Compute_uses_first_letter_of_first_and_last_name_when_both_are_present()
    {
        Assert.Equal("JS", UserInitials.Compute("Jane", "Smith", "jsmith"));
    }

    [Fact]
    public void Compute_uses_first_two_letters_of_first_name_when_last_name_is_missing()
    {
        Assert.Equal("JA", UserInitials.Compute("Jane", null, "jsmith"));
    }

    [Fact]
    public void Compute_uses_a_single_letter_when_the_only_available_name_is_one_character()
    {
        Assert.Equal("J", UserInitials.Compute("J", null, "jsmith"));
    }

    [Fact]
    public void Compute_falls_back_to_username_when_no_name_is_set()
    {
        Assert.Equal("JS", UserInitials.Compute(null, null, "jsmith"));
    }

    [Fact]
    public void Compute_treats_whitespace_only_names_as_absent()
    {
        Assert.Equal("JS", UserInitials.Compute("   ", "  ", "jsmith"));
    }

    [Fact]
    public void Compute_never_returns_more_than_two_characters()
    {
        Assert.Equal(2, UserInitials.Compute("Jonathan", "Livingston", "seagull").Length);
        Assert.Equal(2, UserInitials.Compute("Jonathan", null, "seagull").Length);
        Assert.Equal(2, UserInitials.Compute(null, null, "seagull").Length);
    }
}
