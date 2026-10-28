using System.Net;
using IntoChat.Cloud.Auth.Core;
using IntoChat.Cloud.Auth.Services;
using Microsoft.Extensions.DependencyInjection;

namespace IntoChat.Cloud.Auth.Tests;

public sealed class GoogleLinkSecurityFacts(AuthHost host)
{
    [Fact]
    public async Task GoogleVerifiedEmail_DiscardsPasswordFromPendingUnverifiedSignup()
    {
        var email = AuthHost.UniqueEmail("pending-google");
        using var attacker = host.CookieClient();
        await host.SignUpRequestAsync(attacker, email);
        using var client = host.GoogleClient();
        using var response = await new GoogleFacts(host).CompleteAsync(client, email);
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        var entry = await host.Index(email).Get();
        Assert.False((await host.Credential(entry.PrincipalId!).GetFlags()).HasPassword);
        Assert.Equal(HttpStatusCode.Unauthorized, (await host.PostAsync(attacker, "/auth/login", new { email, password = AuthHost.Password })).StatusCode);
    }

    [Fact]
    public async Task GoogleSubjectMismatch_IsRefusedWithoutSession()
    {
        var email = AuthHost.UniqueEmail("google-subject");
        using var first = host.GoogleClient();
        using var accepted = await new GoogleFacts(host).CompleteAsync(first, email, sub: "first-subject");
        using var second = host.GoogleClient();
        using var rejected = await new GoogleFacts(host).CompleteAsync(second, email, sub: "other-subject");
        Assert.Contains("auth_error=google", rejected.Headers.Location!.OriginalString);
        Assert.Equal(HttpStatusCode.Unauthorized, (await second.GetAsync("/auth/sessions", TestContext.Current.CancellationToken)).StatusCode);
        Assert.Equal("first-subject", (await host.Index(email).Get()).GoogleSub);
    }

    [Fact]
    public async Task TrustedExternalOrigin_IsPreservedThroughAuthenticatorChallenge()
    {
        using var user = await host.SignUpAsync();
        await new TotpFacts(host).EnrolAsync(user);
        var issue = await host.Brain.Services.GetRequiredService<LoginService>().ChallengeExternalAsync(EmailNormalizer.Normalize(user.Email)!, "api");
        var challenge = await host.CodeSlot(user.Email, CodePurpose.Login).GetChallenge(issue!.CodeId!);
        Assert.Equal("api", challenge!.AmrOrigin);
        Assert.Equal(new[] { "authenticator", "recovery" }, challenge.AllowedMethods);
    }
}
