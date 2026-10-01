using JNotes.Core.Users;

namespace JNotes.Tests.Users;

public class UserDisplayNameTests
{
    [Fact]
    public void Format_joins_first_and_last_name()
    {
        Assert.Equal("Jane Smith", UserDisplayName.Format("Jane", "Smith", "jsmith"));
    }

    [Fact]
    public void Format_uses_the_first_name_alone_when_there_is_no_last_name()
    {
        Assert.Equal("Jane", UserDisplayName.Format("Jane", null, "jsmith"));
    }

    [Fact]
    public void Format_uses_the_last_name_alone_when_there_is_no_first_name()
    {
        Assert.Equal("Smith", UserDisplayName.Format(null, "Smith", "jsmith"));
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("", "")]
    [InlineData("   ", "  ")]
    [InlineData(null, "\t")]
    public void Format_falls_back_to_the_username_when_both_names_are_blank(string? first, string? last)
    {
        Assert.Equal("jsmith", UserDisplayName.Format(first, last, "jsmith"));
    }

    [Fact]
    public void Format_trims_the_names()
    {
        Assert.Equal("Jane Smith", UserDisplayName.Format("  Jane ", " Smith  ", "jsmith"));
        Assert.Equal("Jane", UserDisplayName.Format(" Jane ", "   ", "jsmith"));
    }
}
