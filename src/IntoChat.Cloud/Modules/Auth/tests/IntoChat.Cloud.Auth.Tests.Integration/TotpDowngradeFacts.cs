using System.Net;
using IntoChat.Cloud.Mail.Templates;

namespace IntoChat.Cloud.Auth.Tests;

public sealed class TotpDowngradeFacts(AuthHost host)
{
    [Fact]
    public async Task EmailCode_OnTotpEnabledAccount_RequiresAuthenticatorBeforeSession()
    {
        using var user = await host.SignUpAsync();
        await new TotpFacts(host).EnrolAsync(user);
        using var client = host.CookieClient();
        var begin = await AuthHost.ReadAsync(await host.PostAsync(client, "/auth/login", new { email = user.Email, password = user.Password }));
        var id = AuthHost.Parse(begin.Body).GetProperty("challengeId").GetString();
        var verified = await AuthHost.ReadAsync(await host.PostAsync(client, $"/auth/login/challenges/{id}/verify", new { method = "email", code = await host.CodeAsync(user.Email, MailTemplateId.LoginCode) }));
        Assert.Equal(HttpStatusCode.Accepted, verified.Status);
        var methods = AuthHost.Parse(verified.Body).GetProperty("methods").EnumerateArray().Select(item => item.GetString()).ToArray();
        Assert.Equal(new[] { "authenticator", "recovery" }, methods);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/auth/sessions", TestContext.Current.CancellationToken)).StatusCode);
    }
}
