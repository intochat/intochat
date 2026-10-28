using System.Net;
using IntoChat.Cloud.Mail.Templates;

namespace IntoChat.Cloud.Auth.Tests;

public sealed class SignUpSecurityFacts(AuthHost host)
{
    [Fact]
    public async Task StaleSignupCode_AfterGoogleVerificationAndTotpEnrollment_CannotBypassTotp()
    {
        var email = AuthHost.UniqueEmail("stale-signup");
        using var staleClient = host.CookieClient();
        var signup = await AuthHost.ReadAsync(await host.SignUpRequestAsync(staleClient, email));
        var codeId = AuthHost.Parse(signup.Body).GetProperty("codeId").GetString();
        var code = await host.CodeAsync(email, MailTemplateId.SignupCode);
        using var google = host.GoogleClient();
        using var completed = await new GoogleFacts(host).CompleteAsync(google, email);
        var entry = await host.Index(email).Get();
        var owner = new AuthUser(google, email, "", entry.PrincipalId!, entry.AccountId!, entry.BrainId!);
        await new TotpFacts(host).EnrolAsync(owner);
        var result = await AuthHost.ReadAsync(await host.PostAsync(staleClient, $"/auth/codes/{codeId}/verify", new { code }));
        Assert.Equal(HttpStatusCode.Accepted, result.Status);
        Assert.DoesNotContain("email", AuthHost.Parse(result.Body).GetProperty("methods").EnumerateArray().Select(item => item.GetString()));
        Assert.Equal(HttpStatusCode.Unauthorized, (await staleClient.GetAsync("/auth/sessions", TestContext.Current.CancellationToken)).StatusCode);
    }

    [Theory]
    [InlineData("/test/logout")]
    public async Task IdentityLogout_RevokesTheSession_SoCopiedCookieCannotBeReplayed(string logoutPath)
    {
        using var user = await host.SignUpAsync();
        using var client = host.CookieClient();
        var begin = await AuthHost.ReadAsync(await host.PostAsync(client, "/auth/login", new { email = user.Email, password = user.Password }));
        var id = AuthHost.Parse(begin.Body).GetProperty("challengeId").GetString();
        using var verified = await host.PostAsync(client, $"/auth/login/challenges/{id}/verify", new { method = "email", code = await host.CodeAsync(user.Email, MailTemplateId.LoginCode) });
        Assert.Equal(HttpStatusCode.OK, verified.StatusCode);
        var stolen = string.Join("; ", verified.Headers.GetValues("Set-Cookie").Select(cookie => cookie.Split(';')[0]));
        using var replay = new HttpClient(new HttpClientHandler { UseCookies = false }) { BaseAddress = client.BaseAddress };
        replay.DefaultRequestHeaders.Add("Cookie", stolen);
        Assert.Equal(HttpStatusCode.OK, (await replay.GetAsync("/auth/sessions", TestContext.Current.CancellationToken)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await host.PostAsync(client, logoutPath, new { })).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await replay.GetAsync("/auth/sessions", TestContext.Current.CancellationToken)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await user.Client.GetAsync("/auth/sessions", TestContext.Current.CancellationToken)).StatusCode);
    }
}


