using System.Net;
using IntoChat.Cloud.Mail.Templates;

namespace IntoChat.Cloud.Auth.Tests;

public sealed class EmailChangeSecurityFacts(AuthHost host)
{
    [Fact]
    public async Task LockedCode_ReissuedByEngine_RemainsBoundToTheSameReservation()
    {
        using var user = await host.SignUpAsync();
        var email = AuthHost.UniqueEmail("locked-change");
        var begin = await AuthHost.ReadAsync(await host.PostAsync(user.Client, "/auth/email-change", new { newEmail = email }));
        var id = AuthHost.Parse(begin.Body).GetProperty("codeId").GetString();
        var original = await host.CodeAsync(email, MailTemplateId.ChangeEmailCode);
        var wrong = original == "000000" ? "111111" : "000000";
        await host.PostAsync(user.Client, "/auth/email-change/confirm", new { codeId = id, code = wrong });
        await host.PostAsync(user.Client, "/auth/email-change/confirm", new { codeId = id, code = wrong });
        var locked = await AuthHost.ReadAsync(await host.PostAsync(user.Client, "/auth/email-change/confirm", new { codeId = id, code = wrong }));
        Assert.Equal(HttpStatusCode.BadRequest, locked.Status);
        var replacement = AuthHost.Parse(locked.Body).GetProperty("codeId").GetString();
        var code = await host.CodeAsync(email, MailTemplateId.ChangeEmailCode, after: 1);
        Assert.Equal(HttpStatusCode.OK, (await host.PostAsync(user.Client, "/auth/email-change/confirm", new { codeId = replacement, code })).StatusCode);
    }
    [Fact]
    public async Task ProvedMove_InterruptedAfterCredentialCas_ResumesAfterReservationExpiry()
    {
        using var user = await host.SignUpAsync();
        var email = AuthHost.UniqueEmail("resume-change");
        var begin = await AuthHost.ReadAsync(await host.PostAsync(user.Client, "/auth/email-change", new { newEmail = email }));
        var id = AuthHost.Parse(begin.Body).GetProperty("codeId").GetString()!;
        var code = await host.CodeAsync(email, MailTemplateId.ChangeEmailCode);
        Assert.Equal(IntoChat.Cloud.Auth.Neurons.CodeOutcome.Success, (await host.CodeSlot(email, IntoChat.Cloud.Auth.Core.CodePurpose.ChangeEmail).Verify(id, code)).Outcome);
        Assert.True(await host.Index(email).ProveChange(user.PrincipalId, id));
        Assert.True(await host.Credential(user.PrincipalId).TryMoveEmailHash(host.EmailHash(user.Email), host.EmailHash(email)));
        // Simulate a process interruption before CommitChange, then obtain a fresh authenticated session.
        host.Clock.Advance(TimeSpan.FromMinutes(11));
        Assert.True((await host.Index(email).Get()).ChangeProved);
        using var fresh = host.CookieClient();
        var login = await AuthHost.ReadAsync(await host.PostAsync(fresh, "/auth/login", new { email = user.Email, password = user.Password }));
        var loginId = AuthHost.Parse(login.Body).GetProperty("challengeId").GetString();
        Assert.Equal(HttpStatusCode.OK, (await host.PostAsync(fresh, $"/auth/login/challenges/{loginId}/verify", new { method = "email", code = await host.CodeAsync(user.Email, MailTemplateId.LoginCode) })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await host.PostAsync(fresh, "/auth/email-change/confirm", new { codeId = id, code })).StatusCode);
        Assert.Equal(IntoChat.Cloud.Auth.Neurons.EmailIndexStatus.Verified, (await host.Index(email).Get()).Status);
        Assert.Equal(IntoChat.Cloud.Auth.Neurons.EmailIndexStatus.None, (await host.Index(user.Email).Get()).Status);
    }

    [Fact]
    public async Task ConcurrentEmailChanges_HaveExactlyOneWinner()
    {
        using var user = await host.SignUpAsync();
        var first = AuthHost.UniqueEmail("concurrent-first");
        var second = AuthHost.UniqueEmail("concurrent-second");
        var one = await AuthHost.ReadAsync(await host.PostAsync(user.Client, "/auth/email-change", new { newEmail = first }));
        var two = await AuthHost.ReadAsync(await host.PostAsync(user.Client, "/auth/email-change", new { newEmail = second }));
        var id1 = AuthHost.Parse(one.Body).GetProperty("codeId").GetString();
        var id2 = AuthHost.Parse(two.Body).GetProperty("codeId").GetString();
        var code1 = await host.CodeAsync(first, MailTemplateId.ChangeEmailCode);
        var code2 = await host.CodeAsync(second, MailTemplateId.ChangeEmailCode);
        var responses = await Task.WhenAll(
            host.PostAsync(user.Client, "/auth/email-change/confirm", new { codeId = id1, code = code1 }),
            host.PostAsync(user.Client, "/auth/email-change/confirm", new { codeId = id2, code = code2 }));
        Assert.Single(responses, response => response.StatusCode == HttpStatusCode.OK);
        Assert.Equal(1, new[] { (await host.Index(first).Get()).Status, (await host.Index(second).Get()).Status }.Count(status => status == IntoChat.Cloud.Auth.Neurons.EmailIndexStatus.Verified));
        foreach (var response in responses) { response.Dispose(); }
    }
    [Fact]
    public async Task PreviousConsumedProof_CannotAuthorizeNewReservationAfterCooldownRejection()
    {
        using var user = await host.SignUpAsync();
        async Task<(string Id, string Code)> Change(string email)
        {
            var begin = await AuthHost.ReadAsync(await host.PostAsync(user.Client, "/auth/email-change", new { newEmail = email }));
            Assert.Equal(HttpStatusCode.Accepted, begin.Status);
            var id = AuthHost.Parse(begin.Body).GetProperty("codeId").GetString()!;
            var code = await host.CodeAsync(email, MailTemplateId.ChangeEmailCode);
            Assert.Equal(HttpStatusCode.OK, (await host.PostAsync(user.Client, "/auth/email-change/confirm", new { codeId = id, code })).StatusCode);
            return (id, code);
        }
        var first = AuthHost.UniqueEmail("proof-first");
        var second = AuthHost.UniqueEmail("proof-second");
        var old = await Change(first);
        await Change(second);
        Assert.Equal(HttpStatusCode.TooManyRequests, (await host.PostAsync(user.Client, "/auth/email-change", new { newEmail = first })).StatusCode);
        Assert.NotEqual(HttpStatusCode.OK, (await host.PostAsync(user.Client, "/auth/email-change/confirm", new { codeId = old.Id, code = old.Code })).StatusCode);
    }
    [Fact]
    public async Task ExpiredReservation_GoogleVerifiedOwnerGetsNewPrincipal()
    {
        using var reserving = await host.SignUpAsync();
        var email = AuthHost.UniqueEmail("expired-reservation");
        Assert.Equal(HttpStatusCode.Accepted, (await host.PostAsync(reserving.Client, "/auth/email-change", new { newEmail = email })).StatusCode);
        host.Clock.Advance(TimeSpan.FromMinutes(11));
        using var owner = host.GoogleClient();
        using var completed = await new GoogleFacts(host).CompleteAsync(owner, email);
        Assert.Equal(HttpStatusCode.Redirect, completed.StatusCode);
        Assert.NotEqual(reserving.PrincipalId, (await host.Index(email).Get()).PrincipalId);
    }

    [Fact]
    public async Task OverlappingEmailChanges_OnlyOneDestinationCanBecomeVerified()
    {
        using var user = await host.SignUpAsync();
        var first = AuthHost.UniqueEmail("first-change");
        var second = AuthHost.UniqueEmail("second-change");
        var one = await AuthHost.ReadAsync(await host.PostAsync(user.Client, "/auth/email-change", new { newEmail = first }));
        var two = await AuthHost.ReadAsync(await host.PostAsync(user.Client, "/auth/email-change", new { newEmail = second }));
        var id1 = AuthHost.Parse(one.Body).GetProperty("codeId").GetString();
        var id2 = AuthHost.Parse(two.Body).GetProperty("codeId").GetString();
        Assert.Equal(HttpStatusCode.OK, (await host.PostAsync(user.Client, "/auth/email-change/confirm", new { codeId = id1, code = await host.CodeAsync(first, MailTemplateId.ChangeEmailCode) })).StatusCode);
        var next = await host.PostAsync(user.Client, "/auth/email-change/confirm", new { codeId = id2, code = await host.CodeAsync(second, MailTemplateId.ChangeEmailCode) });
        Assert.NotEqual(HttpStatusCode.OK, next.StatusCode);
        using var client = host.CookieClient();
        Assert.Equal(HttpStatusCode.Accepted, (await host.PostAsync(client, "/auth/login", new { email = first, password = user.Password })).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await host.PostAsync(client, "/auth/login", new { email = second, password = user.Password })).StatusCode);
    }
}



