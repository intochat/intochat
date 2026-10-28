using System.Net;
using IntoChat.Cloud.Mail.Templates;

namespace IntoChat.Cloud.Auth.Tests;

// W4 D1 engine rules through the real routes (ported from the archive email-code tests; harness not ported).
public sealed class AuthCodeFacts(AuthHost host)
{
    private async Task<(HttpClient Client, string Email, string CodeId, string Code)> PendingAsync(string name)
    {
        var client = host.CookieClient();
        var email = AuthHost.UniqueEmail(name);
        var (_, body) = await AuthHost.ReadAsync(await host.SignUpRequestAsync(client, email));
        return (client, email, AuthHost.Parse(body).GetProperty("codeId").GetString()!, await host.CodeAsync(email, MailTemplateId.SignupCode));
    }

    private Task<(HttpStatusCode Status, string Body)> VerifyAsync(HttpClient client, string codeId, string code) =>
        host.PostAsync(client, $"/auth/codes/{codeId}/verify", new { code }).ContinueWith(task => AuthHost.ReadAsync(task.Result)).Unwrap();

    private static string Wrong(string code) => code == "000000" ? "000001" : "000000";

    [Fact(Timeout = 120_000)]
    public async Task Verify_WrongCode_InvalidWithAttemptsLeft2()
    {
        var (client, _, codeId, code) = await PendingAsync("wrong");
        using var _client = client;

        var (status, body) = await VerifyAsync(client, codeId, Wrong(code));

        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.Equal("{\"code\":\"EMAIL_CODE_INVALID\",\"attemptsLeft\":2}", body);
    }

    [Fact(Timeout = 120_000)]
    public async Task Verify_ThirdWrong_LockedAndNewCodeMailed()
    {
        var (client, email, codeId, code) = await PendingAsync("locked");
        using var _client = client;
        await VerifyAsync(client, codeId, Wrong(code));
        await VerifyAsync(client, codeId, Wrong(code));

        var (status, body) = await VerifyAsync(client, codeId, Wrong(code));

        Assert.Equal(HttpStatusCode.BadRequest, status);
        var json = AuthHost.Parse(body);
        Assert.Equal("EMAIL_CODE_LOCKED", json.GetProperty("code").GetString());
        var newId = json.GetProperty("codeId").GetString()!;
        Assert.NotEqual(codeId, newId);
        var fresh = await host.CodeAsync(email, MailTemplateId.SignupCode, after: 1);
        Assert.Equal(HttpStatusCode.OK, (await VerifyAsync(client, newId, fresh)).Status);
    }

    [Fact(Timeout = 120_000)]
    public async Task Verify_After10Minutes_ExpiredAndNewCodeMailed()
    {
        var (client, email, codeId, code) = await PendingAsync("expired");
        using var _client = client;
        host.Clock.Advance(TimeSpan.FromMinutes(10));

        var (status, body) = await VerifyAsync(client, codeId, code);

        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.Equal("EMAIL_CODE_EXPIRED", AuthHost.Parse(body).GetProperty("code").GetString());
        await host.MailAsync(email, MailTemplateId.SignupCode, after: 1);
    }

    [Fact(Timeout = 120_000)]
    public async Task Resend_Within42s_Returns429WithRetryAt()
    {
        var (client, email, _, _) = await PendingAsync("cooldown");
        using var _client = client;
        host.Clock.Advance(TimeSpan.FromSeconds(41));

        var (status, body) = await AuthHost.ReadAsync(await host.PostAsync(client, "/auth/codes", new { email, purpose = "signup" }));

        Assert.Equal((HttpStatusCode)429, status);
        var json = AuthHost.Parse(body);
        Assert.Equal("EMAIL_CODE_RATE_LIMITED", json.GetProperty("code").GetString());
        Assert.True(json.TryGetProperty("retryAt", out _));
    }

    [Fact(Timeout = 120_000)]
    public async Task Send_SixthWithinHour_Returns429()
    {
        var (client, email, _, _) = await PendingAsync("hourcap");
        using var _client = client;
        for (var sent = 1; sent < 5; sent++)
        {
            host.Clock.Advance(TimeSpan.FromSeconds(43));
            Assert.Equal(HttpStatusCode.Accepted, (await AuthHost.ReadAsync(await host.PostAsync(client, "/auth/codes", new { email, purpose = "signup" }))).Status);
        }

        host.Clock.Advance(TimeSpan.FromSeconds(43));
        var (status, _) = await AuthHost.ReadAsync(await host.PostAsync(client, "/auth/codes", new { email, purpose = "signup" }));

        Assert.Equal((HttpStatusCode)429, status);
    }

    [Fact(Timeout = 120_000)]
    public async Task Resend_InvalidatesPreviousCode()
    {
        var (client, email, codeId, code) = await PendingAsync("resend");
        using var _client = client;
        host.Clock.Advance(TimeSpan.FromSeconds(43));
        await AuthHost.ReadAsync(await host.PostAsync(client, "/auth/codes", new { email, purpose = "signup" }));

        var (status, _) = await VerifyAsync(client, codeId, code);

        Assert.Equal(HttpStatusCode.NotFound, status);
    }

    [Fact(Timeout = 120_000)]
    public async Task Decoy_NeverNotFound()
    {
        using var client = host.CookieClient();
        var unknown = AuthHost.UniqueEmail("nobody");
        var (_, body) = await AuthHost.ReadAsync(await host.PostAsync(client, "/auth/codes", new { email = unknown, purpose = "signup" }));
        var codeId = AuthHost.Parse(body).GetProperty("codeId").GetString()!;

        var (status, verdict) = await VerifyAsync(client, codeId, "123456");

        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.Equal("{\"code\":\"EMAIL_CODE_INVALID\",\"attemptsLeft\":2}", verdict);
        Assert.Equal(0, host.MailCount(unknown));
    }

    [Fact(Timeout = 120_000)]
    public async Task Verify_UnknownId_Returns404()
    {
        using var client = host.CookieClient();

        var (malformed, _) = await VerifyAsync(client, "not-a-code-id", "123456");
        var (neverIssued, body) = await VerifyAsync(client, "AQAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA", "123456");

        Assert.Equal(HttpStatusCode.NotFound, malformed);
        Assert.Equal(HttpStatusCode.NotFound, neverIssued);
        Assert.Equal("{\"code\":\"EMAIL_CODE_NOT_FOUND\"}", body);
    }
}
