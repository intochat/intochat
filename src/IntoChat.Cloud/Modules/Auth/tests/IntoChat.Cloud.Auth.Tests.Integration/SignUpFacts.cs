using System.Diagnostics;
using System.Net;
using System.Text.RegularExpressions;
using IntoChat.Cloud.Auth.Neurons;
using IntoChat.Cloud.Auth.Services;
using IntoChat.Cloud.Mail.Templates;
using IntoChat.Cloud.Organisations;
using Microsoft.Extensions.DependencyInjection;

namespace IntoChat.Cloud.Auth.Tests;

public sealed partial class SignUpFacts(AuthHost host)
{
    [Fact(Timeout = 120_000)]
    public async Task SignUp_NewUnverifiedVerified_IdenticalResponseShape()
    {
        using var client = host.CookieClient();
        var fresh = AuthHost.UniqueEmail("fresh");
        var unverified = AuthHost.UniqueEmail("pending");
        await AuthHost.ReadAsync(await host.SignUpRequestAsync(client, unverified));
        using var verified = await host.SignUpAsync();
        host.Clock.Advance(TimeSpan.FromSeconds(43));

        var responses = new[]
        {
            await AuthHost.ReadAsync(await host.SignUpRequestAsync(client, fresh)),
            await AuthHost.ReadAsync(await host.SignUpRequestAsync(client, unverified)),
            await AuthHost.ReadAsync(await host.SignUpRequestAsync(client, verified.Email)),
        };

        Assert.All(responses, response => Assert.Equal(HttpStatusCode.Accepted, response.Status));
        var masked = responses.Select(response => Mask(response.Body)).Distinct().ToArray();
        Assert.Single(masked);
    }

    [Fact(Timeout = 120_000)]
    public async Task SignUp_VerifiedEmail_SendsAccountAlreadyExistsNoCode()
    {
        using var user = await host.SignUpAsync();
        var codesBefore = host.MailCount(user.Email, MailTemplateId.SignupCode);
        host.Clock.Advance(TimeSpan.FromSeconds(43));

        var (status, _) = await AuthHost.ReadAsync(await host.SignUpRequestAsync(user.Client, user.Email));

        Assert.Equal(HttpStatusCode.Accepted, status);
        var notice = await host.MailAsync(user.Email, MailTemplateId.AccountAlreadyExists);
        Assert.False(notice.Data.ContainsKey(MailDataKeys.Code));
        Assert.Equal(codesBefore, host.MailCount(user.Email, MailTemplateId.SignupCode));
    }

    [Fact(Timeout = 120_000)]
    public async Task SignUp_ConsentFalse_Returns400UserConsentNotGranted()
    {
        using var client = host.CookieClient();
        var email = AuthHost.UniqueEmail("noconsent");

        var (status, body) = await AuthHost.ReadAsync(await host.SignUpRequestAsync(client, email, consent: false));

        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.Equal("{\"code\":\"USER_CONSENT_NOT_GRANTED\"}", body);
        Assert.Equal(EmailIndexStatus.None, (await host.Index(email).Get()).Status);
    }

    [Fact(Timeout = 120_000)]
    public async Task SignUp_MailDelayed2s_ResponseUnder1s_ForRealAndDecoy()
    {
        using var verified = await host.SignUpAsync();
        host.Clock.Advance(TimeSpan.FromSeconds(43));
        host.Sender.Delay = TimeSpan.FromSeconds(2);
        try
        {
            foreach (var email in new[] { AuthHost.UniqueEmail("timed"), verified.Email })
            {
                var watch = Stopwatch.StartNew();
                var (status, _) = await AuthHost.ReadAsync(await host.SignUpRequestAsync(verified.Client, email));
                watch.Stop();

                Assert.Equal(HttpStatusCode.Accepted, status);
                Assert.True(watch.Elapsed < TimeSpan.FromSeconds(1), $"Sign-up answered in {watch.Elapsed}.");
            }
        }
        finally
        {
            host.Sender.Reset();
        }
    }

    [Fact(Timeout = 120_000)]
    public async Task Verify_CorrectCode_SessionCookieWithSidAndPersonalOrganisation()
    {
        using var user = await host.SignUpAsync();

        // (The host's /identity/session reading the cookie is the host's fact; it lands with the cloud composition in 09-09.)

        // A current row in the sessions table proves the cookie carries the sid the row was created for.
        var (listStatus, listBody) = await AuthHost.ReadAsync(await user.Client.GetAsync("/auth/sessions", TestContext.Current.CancellationToken));
        Assert.Equal(HttpStatusCode.OK, listStatus);
        var row = Assert.Single(AuthHost.Parse(listBody).EnumerateArray());
        Assert.True(row.GetProperty("current").GetBoolean());

        var membership = await host.Membership(user.PrincipalId).Get();
        Assert.NotNull(membership);
        Assert.Equal(user.BrainId, membership.OrgBrain);
        Assert.Equal(EmailIndexStatus.Verified, (await host.Index(user.Email).Get()).Status);
    }

    [Fact(Timeout = 120_000)]
    public async Task SignUp_BeforeVerify_NoOrganisation()
    {
        using var client = host.CookieClient();
        var email = AuthHost.UniqueEmail("unconfirmed");

        await AuthHost.ReadAsync(await host.SignUpRequestAsync(client, email));

        var entry = await host.Index(email).Get();
        Assert.Equal(EmailIndexStatus.Pending, entry.Status);
        Assert.Null(await host.Membership(entry.PrincipalId!).Get());
        Assert.False((await host.Credential(entry.PrincipalId!).GetFlags()).HasPassword);
    }

    [Fact(Timeout = 120_000)]
    public async Task Verify_RetryAfterPartialFailure_CompletesIdempotently()
    {
        using var client = host.CookieClient();
        var email = AuthHost.UniqueEmail("retry");
        var codeId = AuthHost.Parse((await AuthHost.ReadAsync(await host.SignUpRequestAsync(client, email))).Body).GetProperty("codeId").GetString();
        var provisioner = host.Brain.Services.GetRequiredService<AccountProvisioner>();

        // A first completion that stopped before the session was issued (the route never answered).
        var first = await provisioner.CompleteSignUpAsync(host.EmailHash(email));
        var code = await host.CodeAsync(email, MailTemplateId.SignupCode);
        var (status, body) = await AuthHost.ReadAsync(await host.PostAsync(client, $"/auth/codes/{codeId}/verify", new { code }));

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.NotNull(first);
        Assert.Equal(first.PrincipalId, AuthHost.Parse(body).GetProperty("principalId").GetString());
        Assert.Equal(first.BrainId, AuthHost.Parse(body).GetProperty("brainId").GetString());
        var again = await host.Brain.Get<IOrganisationDirectory>(IOrganisationDirectory.Key(first.PrincipalId))
            .EnsurePersonal(new PersonalOrganisationRequest(first.PrincipalId, first.AccountId, first.BrainId, "x", email, "en", DateTimeOffset.UtcNow));
        Assert.False(again.Created);
    }

    [Fact(Timeout = 120_000)]
    public async Task Responses_NeverContainSixDigitString()
    {
        using var client = host.CookieClient();
        var email = AuthHost.UniqueEmail("digits");
        var bodies = new List<string>();
        var signUp = await AuthHost.ReadAsync(await host.SignUpRequestAsync(client, email));
        bodies.Add(signUp.Body);
        var codeId = AuthHost.Parse(signUp.Body).GetProperty("codeId").GetString();
        var code = await host.CodeAsync(email, MailTemplateId.SignupCode);
        bodies.Add((await AuthHost.ReadAsync(await host.PostAsync(client, $"/auth/codes/{codeId}/verify", new { code = code == "000000" ? "000001" : "000000" }))).Body);
        host.Clock.Advance(TimeSpan.FromSeconds(43));
        bodies.Add((await AuthHost.ReadAsync(await host.PostAsync(client, "/auth/codes", new { email, purpose = "signup" }))).Body);
        var fresh = await host.CodeAsync(email, MailTemplateId.SignupCode, after: 1);
        var freshId = AuthHost.Parse(bodies[^1]).GetProperty("codeId").GetString();
        bodies.Add((await AuthHost.ReadAsync(await host.PostAsync(client, $"/auth/codes/{freshId}/verify", new { code = fresh }))).Body);
        bodies.Add((await AuthHost.ReadAsync(await client.GetAsync("/auth/sessions", TestContext.Current.CancellationToken))).Body);

        Assert.All(bodies, body => Assert.DoesNotMatch(SixDigits(), Timestamps().Replace(body, "<t>")));
    }

    internal static string Mask(string body) =>
        Timestamps().Replace(CodeIds().Replace(body, "\"codeId\":\"<id>\""), "<t>");

    [GeneratedRegex(@"\b\d{6}\b")]
    private static partial Regex SixDigits();

    [GeneratedRegex(@"\d{4}-\d{2}-\d{2}T[0-9:.]+(Z|[+-]\d{2}:\d{2})")]
    private static partial Regex Timestamps();

    [GeneratedRegex("\"codeId\":\"[^\"]*\"")]
    private static partial Regex CodeIds();
}
