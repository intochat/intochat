namespace IntoChat.Cloud.Mail.Templates.Tests.Unit;

public sealed class EmailMaskTests
{
    [Fact]
    public void EmailMask_Address_KeepsFirstCharAndDomain() =>
        Assert.Equal("a***@example.com", EmailMask.Mask("anna@example.com"));

    [Theory]
    [InlineData("not-an-email")]
    [InlineData("")]
    [InlineData("@example.com")]
    public void EmailMask_NoAt_ReturnsStars(string value) =>
        Assert.Equal("***", EmailMask.Mask(value));
}
