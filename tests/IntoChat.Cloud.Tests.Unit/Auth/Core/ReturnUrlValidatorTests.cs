using DigitalBrain.Runtime.Http.Surface;

namespace IntoChat.Cloud.Auth.Core.Tests;

public sealed class ReturnUrlValidatorTests
{
    private static readonly ReturnUrlValidator Validator = new(["https://app.intochat.io"]);

    [Theory]
    [InlineData("/portal/home")]
    [InlineData("/")]
    [InlineData("/a?b=c#d")]
    [InlineData("https://app.intochat.io/x")]
    public void IsAllowed_Accepted(string url) => Assert.True(Validator.IsAllowed(url));

    [Theory]
    [InlineData("//evil.com")]
    [InlineData("/\\evil")]
    [InlineData("https://evil.com")]
    [InlineData("javascript:alert(1)")]
    [InlineData("https://app.intochat.io.evil.com/x")]
    [InlineData("https://user@app.intochat.io/x")]
    [InlineData("http://app.intochat.io/x")]
    [InlineData("portal/home")]
    [InlineData("/a\nb")]
    [InlineData("")]
    [InlineData(null)]
    public void IsAllowed_Rejected(string? url) => Assert.False(Validator.IsAllowed(url));
}
