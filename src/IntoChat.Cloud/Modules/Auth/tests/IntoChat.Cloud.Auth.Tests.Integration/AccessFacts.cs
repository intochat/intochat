using System.Net;

namespace IntoChat.Cloud.Auth.Tests;

// R1: stamped requests reach Auth's [Internal] neurons only through AuthCalls, with the SSO hook registered.
public sealed class AccessFacts(AuthHost host)
{
    [Fact(Timeout = 120_000)]
    public async Task SessionsList_FromStampedCookie_WithSignInHandlerRegistered_Succeeds()
    {
        using var user = await host.SignUpAsync();

        var (status, body) = await AuthHost.ReadAsync(await user.Client.GetAsync("/auth/sessions", TestContext.Current.CancellationToken));

        Assert.Equal(HttpStatusCode.OK, status);
        var row = Assert.Single(AuthHost.Parse(body).EnumerateArray());
        Assert.True(row.GetProperty("current").GetBoolean());
        Assert.Equal(1, host.SignIns.CountFor(user.PrincipalId));
        using (AuthHost.AsCaller(user))
        {
            var entries = await host.Brain.Get<IAuthAuditLog>(IAuthAuditLog.Key(user.BrainId)).Read(50);
            Assert.Contains(entries, entry => entry.Kind == AuthAuditKind.SignUp && entry.PrincipalId == user.PrincipalId);
        }
    }

    [Fact(Timeout = 120_000)]
    public async Task Sessions_Anonymous_Returns401()
    {
        using var response = await host.Brain.HttpClient.GetAsync("/auth/sessions", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }
}
