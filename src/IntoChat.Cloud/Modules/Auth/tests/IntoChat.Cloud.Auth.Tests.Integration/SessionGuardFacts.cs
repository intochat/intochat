using System.Net;
using Microsoft.Extensions.DependencyInjection;

namespace IntoChat.Cloud.Auth.Tests;

public sealed class SessionGuardFacts(AuthHost host)
{
    [Fact]
    public async Task RevokedCookie_IsRejectedOnNextRequest()
    {
        using var user = await host.SignUpAsync();
        var row = Assert.Single(await host.Sessions(user.PrincipalId).List(null));
        await host.Sessions(user.PrincipalId).RevokeByHandle(row.Handle);
        Assert.Equal(HttpStatusCode.Unauthorized, (await user.Client.GetAsync("/auth/sessions", TestContext.Current.CancellationToken)).StatusCode);
    }
}
