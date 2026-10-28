using System.Net;
using IntoChat.Cloud.Mail.Templates;

namespace IntoChat.Cloud.Auth.Tests;

public sealed class LoginChallengeFacts(AuthHost host)
{
    [Fact]
    public async Task Login_ValidPassword_ReturnsChallengeWithoutCookie_AndEmailVerificationIssuesSession()
    {
        using var user = await host.SignUpAsync();
        using var client = host.CookieClient();
        var (status, body) = await AuthHost.ReadAsync(await host.PostAsync(client, "/auth/login", new { email = user.Email, password = user.Password }));
        Assert.Equal(HttpStatusCode.Accepted, status);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/auth/sessions", TestContext.Current.CancellationToken)).StatusCode);
        var id = AuthHost.Parse(body).GetProperty("challengeId").GetString();
        var code = await host.CodeAsync(user.Email, MailTemplateId.LoginCode);
        var response = await host.PostAsync(client, $"/auth/login/challenges/{id}/verify", new { method = "email", code });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/auth/sessions", TestContext.Current.CancellationToken)).StatusCode);
        Assert.NotEqual(HttpStatusCode.OK, (await host.PostAsync(client, $"/auth/login/challenges/{id}/verify", new { method = "email", code })).StatusCode);
    }

    [Fact]
    public async Task Login_WrongPasswordAndUnknownEmail_Identical401()
    {
        using var user = await host.SignUpAsync();
        using var client = host.CookieClient();
        var known = await AuthHost.ReadAsync(await host.PostAsync(client, "/auth/login", new { email = user.Email, password = "incorrect password" }));
        var unknown = await AuthHost.ReadAsync(await host.PostAsync(client, "/auth/login", new { email = AuthHost.UniqueEmail("unknown"), password = "incorrect password" }));
        Assert.Equal(HttpStatusCode.Unauthorized, known.Status);
        Assert.Equal(known, unknown);
    }

    [Fact]
    public async Task Login_FiveFailures_LocksThenClockUnlocks()
    {
        using var user = await host.SignUpAsync();
        using var client = host.CookieClient();
        for (var i = 0; i < 5; i++)
        {
            await host.PostAsync(client, "/auth/login", new { email = user.Email, password = "incorrect password" });
        }
        Assert.Equal(HttpStatusCode.Locked, (await host.PostAsync(client, "/auth/login", new { email = user.Email, password = user.Password })).StatusCode);
        host.Clock.Advance(TimeSpan.FromMinutes(16));
        Assert.Equal(HttpStatusCode.Accepted, (await host.PostAsync(client, "/auth/login", new { email = user.Email, password = user.Password })).StatusCode);
    }
}

public sealed class ResetFacts(AuthHost host)
{
    [Theory]
    [InlineData(11, 12)]
    [InlineData(257, 256)]
    public async Task Reset_rejects_invalid_length_without_spending_ticket(int invalidLength, int validLength)
    {
        using var user = await host.SignUpAsync();
        using var client = host.CookieClient();
        var request = await AuthHost.ReadAsync(await host.PostAsync(client, "/auth/password-reset", new { email = user.Email }));
        var codeId = AuthHost.Parse(request.Body).GetProperty("codeId").GetString();
        var code = await host.CodeAsync(user.Email, MailTemplateId.ResetCode);
        var verified = await AuthHost.ReadAsync(await host.PostAsync(client, $"/auth/codes/{codeId}/verify", new { code }));
        var ticket = AuthHost.Parse(verified.Body).GetProperty("ticket").GetString();
        Assert.Equal(HttpStatusCode.BadRequest, (await host.PostAsync(client, "/auth/password-reset/complete", new { email = user.Email, ticket, newPassword = new string('x', invalidLength) })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await host.PostAsync(client, "/auth/password-reset/complete", new { email = user.Email, ticket, newPassword = new string('x', validLength) })).StatusCode);
    }

    [Fact]
    public async Task Reset_TicketRotatesPassword_RevokesSessions_AndIsSingleUse()
    {
        using var user = await host.SignUpAsync();
        using var client = host.CookieClient();
        var request = await AuthHost.ReadAsync(await host.PostAsync(client, "/auth/password-reset", new { email = user.Email }));
        Assert.Equal(HttpStatusCode.Accepted, request.Status);
        var codeId = AuthHost.Parse(request.Body).GetProperty("codeId").GetString();
        var code = await host.CodeAsync(user.Email, MailTemplateId.ResetCode);
        var verified = await AuthHost.ReadAsync(await host.PostAsync(client, $"/auth/codes/{codeId}/verify", new { code }));
        Assert.Equal(HttpStatusCode.OK, verified.Status);
        var ticket = AuthHost.Parse(verified.Body).GetProperty("ticket").GetString();
        var completion = new { email = user.Email, ticket, newPassword = "new correct horse battery" };
        Assert.Equal(HttpStatusCode.OK, (await host.PostAsync(client, "/auth/password-reset/complete", completion)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await host.PostAsync(client, "/auth/password-reset/complete", completion)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await user.Client.GetAsync("/auth/sessions", TestContext.Current.CancellationToken)).StatusCode);
        var rows = await host.Sessions(user.PrincipalId).List(null);
        Assert.Single(rows);
        Assert.Equal(HttpStatusCode.Unauthorized, (await host.PostAsync(client, "/auth/login", new { email = user.Email, password = user.Password })).StatusCode);
        Assert.Equal(HttpStatusCode.Accepted, (await host.PostAsync(client, "/auth/login", new { email = user.Email, password = completion.newPassword })).StatusCode);
    }
}

public sealed class EmailChangeFacts(AuthHost host)
{
    [Fact]
    public async Task ChangeEmail_MailsNewAddress_AndMovesLogin()
    {
        using var user = await host.SignUpAsync();
        var newEmail = AuthHost.UniqueEmail("changed");
        var request = await AuthHost.ReadAsync(await host.PostAsync(user.Client, "/auth/email-change", new { newEmail }));
        Assert.Equal(HttpStatusCode.Accepted, request.Status);
        var codeId = AuthHost.Parse(request.Body).GetProperty("codeId").GetString();
        var code = await host.CodeAsync(newEmail, MailTemplateId.ChangeEmailCode);
        Assert.Equal(HttpStatusCode.OK, (await host.PostAsync(user.Client, "/auth/email-change/confirm", new { codeId, code })).StatusCode);
        using var client = host.CookieClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await host.PostAsync(client, "/auth/login", new { email = user.Email, password = user.Password })).StatusCode);
        Assert.Equal(HttpStatusCode.Accepted, (await host.PostAsync(client, "/auth/login", new { email = newEmail, password = user.Password })).StatusCode);
    }

    [Fact]
    public async Task ChangeEmail_StaleSession_RequiresReauthentication()
    {
        using var user = await host.SignUpAsync();
        host.Clock.Advance(TimeSpan.FromMinutes(11));
        var response = await AuthHost.ReadAsync(await host.PostAsync(user.Client, "/auth/email-change", new { newEmail = AuthHost.UniqueEmail("stale") }));
        Assert.Equal(HttpStatusCode.Unauthorized, response.Status);
        Assert.Contains("REAUTHENTICATION_REQUIRED", response.Body);
    }
}

