using IntoChat.Cloud.Auth.Core;

namespace IntoChat.Cloud.Auth.Core.Tests;

public sealed class PasswordHasherTests
{
    [Fact]
    public void DefaultIterations_Is600000()
    {
        Assert.Equal(600_000, PasswordHasher.DefaultIterations);
        Assert.Equal(600_000, new PasswordHasher().Iterations);
    }

    [Fact]
    public void Hash_UsesVersionedFormat()
    {
        var parts = new PasswordHasher(1000).Hash("correct horse battery").Split('$');

        Assert.Equal(4, parts.Length);
        Assert.Equal("pbkdf2-sha256", parts[0]);
        Assert.Equal("1000", parts[1]);
        Assert.Equal(16, Convert.FromBase64String(parts[2]).Length);
        Assert.Equal(32, Convert.FromBase64String(parts[3]).Length);
    }

    [Fact]
    public void Verify_CorrectPassword_Succeeds()
    {
        var hasher = new PasswordHasher(1000);

        Assert.Equal(PasswordVerification.Success, hasher.Verify("correct horse battery", hasher.Hash("correct horse battery")));
    }

    [Fact]
    public void Verify_WrongPassword_Fails()
    {
        var hasher = new PasswordHasher(1000);

        Assert.Equal(PasswordVerification.Failed, hasher.Verify("wrong horse battery", hasher.Hash("correct horse battery")));
    }

    [Fact]
    public void Verify_MalformedStoredValue_Fails()
    {
        var hasher = new PasswordHasher(1000);

        Assert.Equal(PasswordVerification.Failed, hasher.Verify("x", "garbage"));
        Assert.Equal(PasswordVerification.Failed, hasher.Verify("x", "pbkdf2-sha256$0$AA==$AA=="));
        Assert.Equal(PasswordVerification.Failed, hasher.Verify("x", null));
    }

    [Fact]
    public void Verify_StoredIterationsBelowConfigured_ReportsRehashNeeded()
    {
        var stored = new PasswordHasher(1000).Hash("correct horse battery");

        Assert.Equal(PasswordVerification.SuccessRehashNeeded, new PasswordHasher(2000).Verify("correct horse battery", stored));
    }

    [Fact]
    public void Hash_SamePasswordTwice_UsesDifferentSalt()
    {
        var hasher = new PasswordHasher(1000);

        Assert.NotEqual(hasher.Hash("correct horse battery"), hasher.Hash("correct horse battery"));
    }

    [Theory]
    [InlineData(11, false)]
    [InlineData(12, true)]
    [InlineData(256, true)]
    [InlineData(257, false)]
    public void IsValidLength_Boundaries(int length, bool expected)
    {
        Assert.Equal(expected, PasswordHasher.IsValidLength(new string('a', length)));
    }
}
