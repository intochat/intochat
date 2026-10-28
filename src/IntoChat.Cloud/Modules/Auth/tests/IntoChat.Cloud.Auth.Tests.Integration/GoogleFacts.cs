using System.Net;
using System.Text.Json;
using IntoChat.Cloud.Auth.Neurons;
using Microsoft.AspNetCore.WebUtilities;

namespace IntoChat.Cloud.Auth.Tests;

public sealed class GoogleFacts(AuthHost host)
{
    internal async Task<HttpResponseMessage> CompleteAsync(HttpClient client, string email, bool verified = true, string? sub = null, string returnUrl = "/")
    {
        host.Google.UserInfoJson = JsonSerializer.Serialize(new { sub = sub ?? "google-" + email, email, email_verified = verified, name = "Google Person" });
        using var start = await client.GetAsync("/auth/google/start?returnUrl=" + Uri.EscapeDataString(returnUrl), TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Redirect, start.StatusCode);
        Assert.Equal("accounts.google.com", start.Headers.Location!.Host);
        var state = QueryHelpers.ParseQuery(start.Headers.Location.Query)["state"].ToString();
        using var callback = await client.GetAsync("/auth/google/callback?code=scripted-code&state=" + Uri.EscapeDataString(state), TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Redirect, callback.StatusCode);
        return await client.GetAsync(callback.Headers.Location, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Start_RedirectsWithStateAndCorrelation_RejectsOffOriginReturn()
    {
        using var client = host.GoogleClient();
        using var start = await client.GetAsync("/auth/google/start", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Redirect, start.StatusCode);
        Assert.Contains("state=", start.Headers.Location!.Query);
        Assert.Contains(start.Headers.GetValues("Set-Cookie"), cookie => cookie.Contains("Correlation", StringComparison.Ordinal));
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync("/auth/google/start?returnUrl=https://evil.example/", TestContext.Current.CancellationToken)).StatusCode);
    }

    [Fact]
    public async Task VerifiedGoogle_CreatesOneAccount_UsesSessionIssuer_AndClearsExternalCookie()
    {
        var email = AuthHost.UniqueEmail("google");
        using var client = host.GoogleClient();
        using var complete = await CompleteAsync(client, email);
        Assert.Equal(HttpStatusCode.Redirect, complete.StatusCode);
        var entry = await host.Index(email).Get();
        Assert.Equal(EmailIndexStatus.Verified, entry.Status);
        Assert.False((await host.Credential(entry.PrincipalId!).GetFlags()).HasPassword);
        Assert.Equal(1, host.SignIns.CountFor(entry.PrincipalId!));
        Assert.Contains(complete.Headers.GetValues("Set-Cookie"), cookie => cookie.StartsWith("auth.external=;", StringComparison.Ordinal));
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/auth/sessions", TestContext.Current.CancellationToken)).StatusCode);
        using var again = await CompleteAsync(client, email);
        Assert.Equal(entry.PrincipalId, (await host.Index(email).Get()).PrincipalId);
    }

    [Fact]
    public async Task UnverifiedGoogle_IsRefused_AndExistingVerifiedEmailLinksSamePrincipal()
    {
        using var client = host.GoogleClient();
        var unverified = AuthHost.UniqueEmail("unverified");
        using var refused = await CompleteAsync(client, unverified, false);
        Assert.Contains("auth_error=google", refused.Headers.Location!.OriginalString);
        Assert.Equal(EmailIndexStatus.None, (await host.Index(unverified).Get()).Status);
        using var user = await host.SignUpAsync();
        using var linked = await CompleteAsync(client, user.Email);
        Assert.Equal(user.PrincipalId, (await host.Index(user.Email).Get()).PrincipalId);
    }

    [Fact]
    public async Task GoogleWithTotp_OnlyAuthenticatorOrRecovery_NoSessionUntilVerified()
    {
        using var user = await host.SignUpAsync();
        var (secret, _) = await new TotpFacts(host).EnrolAsync(user);
        using var client = host.GoogleClient();
        using var complete = await CompleteAsync(client, user.Email);
        var id = QueryHelpers.ParseQuery(new Uri(client.BaseAddress!, complete.Headers.Location!).Query)["challengeId"].ToString();
        Assert.False(string.IsNullOrWhiteSpace(id));
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/auth/sessions", TestContext.Current.CancellationToken)).StatusCode);
        var email = await AuthHost.ReadAsync(await host.PostAsync(client, $"/auth/login/challenges/{id}/verify", new { method = "email", code = "123456" }));
        Assert.Equal(HttpStatusCode.BadRequest, email.Status);
        Assert.Contains("METHOD_NOT_ALLOWED", email.Body);
        host.Clock.Advance(TimeSpan.FromSeconds(30));
        Assert.Equal(HttpStatusCode.OK, (await host.PostAsync(client, $"/auth/login/challenges/{id}/verify", new { method = "authenticator", code = TotpFacts.Code(secret, host.Clock.GetUtcNow()) })).StatusCode);
    }
}
