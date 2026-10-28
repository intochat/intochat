using System.Net;
using IntoChat.Cloud.Mail.Templates;
using OtpNet;

namespace IntoChat.Cloud.Auth.Tests;

public sealed class TotpFacts(AuthHost host)
{
    internal static string Code(string secret, DateTimeOffset time) => new Totp(Base32Encoding.ToBytes(secret)).ComputeTotp(time.UtcDateTime);

    internal async Task<(string Secret, string[] Recovery)> EnrolAsync(AuthUser user)
    {
        var begin = await AuthHost.ReadAsync(await host.PostAsync(user.Client, "/auth/totp", new { }));
        Assert.Equal(HttpStatusCode.OK, begin.Status);
        var secret = AuthHost.Parse(begin.Body).GetProperty("secret").GetString()!;
        Assert.Contains("otpauth://", begin.Body);
        var confirm = await AuthHost.ReadAsync(await host.PostAsync(user.Client, "/auth/totp/confirm", new { code = Code(secret, host.Clock.GetUtcNow()) }));
        Assert.Equal(HttpStatusCode.OK, confirm.Status);
        var recovery = AuthHost.Parse(confirm.Body).GetProperty("recoveryCodes").EnumerateArray().Select(item => item.GetString()!).ToArray();
        Assert.Equal(10, recovery.Length);
        return (secret, recovery);
    }

    [Fact]
    public async Task Enrol_RecoveryCodesReturnedOnce_SecretNotInState_AndReplayRejected()
    {
        using var user = await host.SignUpAsync();
        var (secret, recovery) = await EnrolAsync(user);
        Assert.True((await host.Credential(user.PrincipalId).GetFlags()).HasTotp);
        Assert.DoesNotContain(secret, string.Join(Environment.NewLine, host.Logs.Lines));
        Assert.All(typeof(IntoChat.Cloud.Auth.Neurons.AuthCredentialState).GetProperties(), property => Assert.False(property.Name.Contains("Totp", StringComparison.Ordinal) && property.PropertyType == typeof(string)));
        Assert.Equal(HttpStatusCode.BadRequest, (await host.PostAsync(user.Client, "/auth/totp/confirm", new { code = Code(secret, host.Clock.GetUtcNow()) })).StatusCode);
        using var client = host.CookieClient();
        var login = await AuthHost.ReadAsync(await host.PostAsync(client, "/auth/login", new { email = user.Email, password = user.Password }));
        var challenge = AuthHost.Parse(login.Body);
        Assert.Equal(3, challenge.GetProperty("methods").GetArrayLength());
        var id = challenge.GetProperty("challengeId").GetString();
        var replay = await host.PostAsync(client, $"/auth/login/challenges/{id}/verify", new { method = "authenticator", code = Code(secret, host.Clock.GetUtcNow()) });
        Assert.Equal(HttpStatusCode.BadRequest, replay.StatusCode);
        host.Clock.Advance(TimeSpan.FromSeconds(30));
        var success = await host.PostAsync(client, $"/auth/login/challenges/{id}/verify", new { method = "authenticator", code = Code(secret, host.Clock.GetUtcNow()) });
        Assert.Equal(HttpStatusCode.OK, success.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await host.PostAsync(user.Client, "/auth/totp/disable", new { recoveryCode = "invalid" })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await host.PostAsync(user.Client, "/auth/totp/disable", new { recoveryCode = recovery[0] })).StatusCode);
        Assert.False((await host.Credential(user.PrincipalId).GetFlags()).HasTotp);
    }

    [Fact]
    public async Task AuthenticatorAttempts_SpendChallengeBudget_AndRecoveryIsSingleUse()
    {
        using var user = await host.SignUpAsync();
        var (_, recovery) = await EnrolAsync(user);
        using var client = host.CookieClient();
        var login = await AuthHost.ReadAsync(await host.PostAsync(client, "/auth/login", new { email = user.Email, password = user.Password }));
        var id = AuthHost.Parse(login.Body).GetProperty("challengeId").GetString();
        for (var i = 0; i < 3; i++)
        {
            Assert.Equal(HttpStatusCode.BadRequest, (await host.PostAsync(client, $"/auth/login/challenges/{id}/verify", new { method = "authenticator", code = "xxxxxx" })).StatusCode);
        }
        var locked = await AuthHost.ReadAsync(await host.PostAsync(client, $"/auth/login/challenges/{id}/verify", new { method = "recovery", code = recovery[0] }));
        Assert.Equal(HttpStatusCode.BadRequest, locked.Status);
        Assert.Contains("LOCKED", locked.Body);
        Assert.True(await host.Credential(user.PrincipalId).UseRecovery(recovery[0]));
        Assert.False(await host.Credential(user.PrincipalId).UseRecovery(recovery[0]));


    }

    [Fact]
    public async Task Disable_ExpiredRecentAuthentication_RejectsEvenFreshCurrentCode()
    {
        using var user = await host.SignUpAsync();
        var (secret, _) = await EnrolAsync(user);
        host.Clock.Advance(TimeSpan.FromMinutes(11));
        var response = await AuthHost.ReadAsync(await host.PostAsync(user.Client, "/auth/totp/disable", new { code = Code(secret, host.Clock.GetUtcNow()) }));
        Assert.Equal(HttpStatusCode.Unauthorized, response.Status);
        Assert.Contains("REAUTHENTICATION_REQUIRED", response.Body);
        Assert.True((await host.Credential(user.PrincipalId).GetFlags()).HasTotp);
    }

    [Fact]
    public async Task ResetComplete_TotpEnabled_NoSession_AndDisableRequiresRecentAuth()
    {
        using var user = await host.SignUpAsync();
        var (_, recovery) = await EnrolAsync(user);
        using var client = host.CookieClient();
        var request = await AuthHost.ReadAsync(await host.PostAsync(client, "/auth/password-reset", new { email = user.Email }));
        var codeId = AuthHost.Parse(request.Body).GetProperty("codeId").GetString();
        var verified = await AuthHost.ReadAsync(await host.PostAsync(client, $"/auth/codes/{codeId}/verify", new { code = await host.CodeAsync(user.Email, MailTemplateId.ResetCode) }));
        var ticket = AuthHost.Parse(verified.Body).GetProperty("ticket").GetString();
        var completion = await AuthHost.ReadAsync(await host.PostAsync(client, "/auth/password-reset/complete", new { email = user.Email, ticket, newPassword = "new correct horse battery" }));
        Assert.Equal(HttpStatusCode.OK, completion.Status);
        Assert.False(AuthHost.Parse(completion.Body).GetProperty("signedIn").GetBoolean());
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/auth/sessions", TestContext.Current.CancellationToken)).StatusCode);
        host.Clock.Advance(TimeSpan.FromMinutes(11));
        Assert.Equal(HttpStatusCode.Unauthorized, (await host.PostAsync(user.Client, "/auth/totp/disable", new { recoveryCode = recovery[0] })).StatusCode);
    }
}


