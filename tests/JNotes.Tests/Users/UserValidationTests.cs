using JNotes.Core.Users;

namespace JNotes.Tests.Users;

public class UserValidationTests
{
    [Theory]
    [InlineData("alice@example.com")]
    [InlineData("a.b-c_d@sub.example.co.uk")]
    public void IsValidEmail_accepts_well_formed_addresses(string email)
    {
        Assert.True(UserValidation.IsValidEmail(email));
    }

    [Theory]
    [InlineData("not-an-email")]
    [InlineData("missing-domain@")]
    [InlineData("@missing-local.com")]
    [InlineData("no-at-sign.example.com")]
    [InlineData("no-dot@example")]
    [InlineData("")]
    public void IsValidEmail_rejects_malformed_addresses(string email)
    {
        Assert.False(UserValidation.IsValidEmail(email));
    }

    [Theory]
    [InlineData("alice")]
    [InlineData("alice-bob_2")]
    public void IsLowercase_accepts_lowercase_usernames(string username)
    {
        Assert.True(UserValidation.IsLowercase(username));
    }

    [Theory]
    [InlineData("Alice")]
    [InlineData("ALICE")]
    [InlineData("aliCe")]
    public void IsLowercase_rejects_usernames_containing_uppercase(string username)
    {
        Assert.False(UserValidation.IsLowercase(username));
    }
}
