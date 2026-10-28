using IntoChat.Cloud.Mail.Templates;

namespace IntoChat.Cloud.Mail.Templates.Tests.Unit;

public sealed class MailLogRedactionTests
{
    private static readonly MailTemplateRenderer Renderer = new();

    [Theory]
    [InlineData("https://app.intochat.io/invite#tok_ABC123", "https://app.intochat.io/invite<redacted>")]
    [InlineData("https://app.intochat.io/invite/tok_ABC123", "https://app.intochat.io/invite/<redacted>")]
    [InlineData("https://app.intochat.io/verify?t=tok_ABC123", "https://app.intochat.io/verify<redacted>")]
    [InlineData("https://app.intochat.io/login", "https://app.intochat.io/login")]
    public void RedactUrl_TokenShapes_CutsTokenAway(string url, string expected)
    {
        Assert.Equal(expected, MailLogRedaction.RedactUrl(url));
    }

    [Fact]
    public void RedactBody_SignupCode_HidesCode()
    {
        var data = new Dictionary<string, string>
        {
            [MailDataKeys.Code] = "654321",
            [MailDataKeys.ActionUrl] = "https://app.intochat.io/signup",
        };
        var rendered = Renderer.Render(MailTemplateId.SignupCode, "en", data);

        var body = MailLogRedaction.RedactBody(rendered.PlainText, data);

        Assert.DoesNotContain("654321", body);
        Assert.Contains(MailLogRedaction.CodeMask, body);
    }

    [Fact]
    public void RedactBody_TeamInvite_HidesInviteToken()
    {
        var data = new Dictionary<string, string>
        {
            [MailDataKeys.InviterName] = "Alice",
            [MailDataKeys.OrganisationName] = "Acme",
            [MailDataKeys.ActionUrl] = "https://app.intochat.io/invite#tok_ABC123",
        };
        var rendered = Renderer.Render(MailTemplateId.TeamInvite, "en", data);

        var body = MailLogRedaction.RedactBody(rendered.PlainText, data);

        Assert.DoesNotContain("tok_ABC123", body);
        Assert.Contains(MailLogRedaction.TokenMask, body);
    }
}
