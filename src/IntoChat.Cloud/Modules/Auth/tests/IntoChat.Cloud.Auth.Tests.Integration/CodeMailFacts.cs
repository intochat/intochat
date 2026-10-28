using System.Net;
using IntoChat.Cloud.Auth.Neurons;
using IntoChat.Cloud.Auth.Services;
using IntoChat.Cloud.Mail;
using IntoChat.Cloud.Mail.Templates;
using Microsoft.Extensions.DependencyInjection;

namespace IntoChat.Cloud.Auth.Tests;

// R4: the mail Auth builds renders through the real templates, and no mail outcome changes the HTTP response.
public sealed class CodeMailFacts(AuthHost host)
{
    [Fact(Timeout = 120_000)]
    public void AllCodeTemplates_RenderWithCodeMailerData()
    {
        var mailer = host.Brain.Services.GetRequiredService<CodeMailer>();
        var renderer = new MailTemplateRenderer();
        MailTemplateId[] codeTemplates = [MailTemplateId.SignupCode, MailTemplateId.LoginCode, MailTemplateId.ResetCode, MailTemplateId.ChangeEmailCode];
        foreach (var language in new[] { "en", "ru" })
        {
            foreach (var template in codeTemplates)
            {
                var message = mailer.BuildMessage(new CodeDelivery("render@example.com", language, "Render", template, null), "123456");
                var rendered = renderer.Render(message.Template, message.Language, message.Data);
                Assert.Contains("123456", rendered.Html);
                Assert.Contains(AuthHost.PublicBaseUrl + "/auth", rendered.PlainText);
            }

            var notice = mailer.BuildMessage(new CodeDelivery("render@example.com", language, "Render", MailTemplateId.SignupCode, MailTemplateId.AccountAlreadyExists), null);
            Assert.Equal(MailTemplateId.AccountAlreadyExists, notice.Template);
            Assert.False(notice.Data.ContainsKey(MailDataKeys.Code));
            Assert.Contains(AuthHost.PublicBaseUrl + "/auth", renderer.Render(notice.Template, notice.Language, notice.Data).PlainText);
        }
    }

    [Theory(Timeout = 120_000)]
    [InlineData(MailSendOutcome.RateLimited, AuthAuditKind.MailRateLimited)]
    [InlineData(MailSendOutcome.Failed, AuthAuditKind.MailFailed)]
    [InlineData(MailSendOutcome.NotConfigured, AuthAuditKind.MailNotConfigured)]
    public async Task MailOutcome_SameResponse_AuditedMasked(MailSendOutcome outcome, AuthAuditKind kind)
    {
        using var client = host.CookieClient();
        var email = AuthHost.UniqueEmail("mailoutcome");
        var before = (await host.SystemAudit.Read(kind, 500)).Length;
        host.Sender.Outcome = outcome;
        try
        {
            var (status, body) = await AuthHost.ReadAsync(await host.SignUpRequestAsync(client, email));

            Assert.Equal(HttpStatusCode.Accepted, status);
            Assert.True(AuthHost.Parse(body).TryGetProperty("codeId", out _));
            var entries = await WaitForAsync(kind, before);
            Assert.All(entries, entry => Assert.DoesNotContain("@", entry.Outcome + entry.Detail + entry.PrincipalId));
            Assert.DoesNotContain(host.Logs.Lines, line => line.Contains(email, StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            host.Sender.Reset();
        }
    }

    private async Task<AuthAuditEntry[]> WaitForAsync(AuthAuditKind kind, int before)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (true)
        {
            var entries = await host.SystemAudit.Read(kind, 500);
            if (entries.Length > before)
            {
                return entries;
            }

            Assert.True(DateTime.UtcNow < deadline, $"No {kind} audit entry.");
            await Task.Delay(25, TestContext.Current.CancellationToken);
        }
    }
}
