using System.Net;
using IntoChat.Cloud.Auth.Core;
using IntoChat.Cloud.Auth.Neurons;
using IntoChat.Cloud.Mail.Templates;

namespace IntoChat.Cloud.Auth.Tests;

// The archive's concurrency tests, as parallel calls against one neuron: its turn is the lock.
public sealed class AuthCodeConcurrencyFacts(AuthHost host)
{
    private async Task<(string Email, string CodeId, string Code)> PendingAsync(string name)
    {
        using var client = host.CookieClient();
        var email = AuthHost.UniqueEmail(name);
        var (_, body) = await AuthHost.ReadAsync(await host.SignUpRequestAsync(client, email));
        return (email, AuthHost.Parse(body).GetProperty("codeId").GetString()!, await host.CodeAsync(email, MailTemplateId.SignupCode));
    }

    [Fact(Timeout = 120_000)]
    public async Task ParallelWrong_ExactlyThreeAttemptsCountedAndOneLocked()
    {
        var (email, codeId, code) = await PendingAsync("parallelwrong");
        var slot = host.CodeSlot(email, CodePurpose.Signup);
        var wrong = code == "000000" ? "000001" : "000000";

        var verdicts = await Task.WhenAll(Enumerable.Range(0, 10).Select(_ => slot.Verify(codeId, wrong)));

        Assert.Equal(2, verdicts.Count(v => v.Outcome == CodeOutcome.Invalid));
        Assert.Equal(1, verdicts.Count(v => v.Outcome == CodeOutcome.Locked));
        Assert.Equal(7, verdicts.Count(v => v.Outcome == CodeOutcome.NotFound));
    }

    [Fact(Timeout = 120_000)]
    public async Task ParallelCorrect_ExactlyOneSuccess()
    {
        var (email, codeId, code) = await PendingAsync("parallelright");
        var slot = host.CodeSlot(email, CodePurpose.Signup);

        var verdicts = await Task.WhenAll(Enumerable.Range(0, 10).Select(_ => slot.Verify(codeId, code)));

        Assert.Equal(1, verdicts.Count(v => v.Outcome == CodeOutcome.Success));
    }

    [Fact(Timeout = 120_000)]
    public async Task ParallelSignUpVerify_OnePrincipal()
    {
        var (email, codeId, code) = await PendingAsync("parallelverify");

        var responses = await Task.WhenAll(Enumerable.Range(0, 10).Select(async _ =>
        {
            using var client = host.CookieClient();
            return await AuthHost.ReadAsync(await host.PostAsync(client, $"/auth/codes/{codeId}/verify", new { code }));
        }));

        var ok = Assert.Single(responses, response => response.Status == HttpStatusCode.OK);
        var entry = await host.Index(email).Get();
        Assert.Equal(EmailIndexStatus.Verified, entry.Status);
        Assert.Equal(entry.PrincipalId, AuthHost.Parse(ok.Body).GetProperty("principalId").GetString());
    }
}
