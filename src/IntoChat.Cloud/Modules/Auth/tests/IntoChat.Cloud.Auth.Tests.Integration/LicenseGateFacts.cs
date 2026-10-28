using System.Net;
using IntoChat.OS.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace IntoChat.Cloud.Auth.Tests;

public sealed class LicenseGateFacts(AuthHost host)
{
    [Fact(Timeout = 120_000)]
    public async Task License_Anonymous_Returns401()
    {
        using var response = await host.Brain.HttpClient.GetAsync("/auth/license", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact(Timeout = 120_000)]
    public async Task License_OnlyListedOperators_SeeHolderNeverToken()
    {
        var options = host.Brain.Services.GetRequiredService<AuthModuleOptions>();
        using var operatorUser = await host.SignUpAsync();
        using var other = await host.SignUpAsync();
        var ct = TestContext.Current.CancellationToken;

        var (emptyStatus, _) = await AuthHost.ReadAsync(await operatorUser.Client.GetAsync("/auth/license", ct));
        options.Operators = [operatorUser.PrincipalId];
        try
        {
            var (status, body) = await AuthHost.ReadAsync(await operatorUser.Client.GetAsync("/auth/license", ct));
            var (otherStatus, _) = await AuthHost.ReadAsync(await other.Client.GetAsync("/auth/license", ct));

            Assert.Equal(HttpStatusCode.NotFound, emptyStatus);
            Assert.Equal(HttpStatusCode.OK, status);
            Assert.Equal("Test Org", AuthHost.Parse(body).GetProperty("license").GetProperty("holder").GetString());
            Assert.Equal("valid", AuthHost.Parse(body).GetProperty("license").GetProperty("status").GetString());
            Assert.DoesNotContain(options.License!, body);
            Assert.DoesNotContain(".", AuthHost.Parse(body).GetProperty("license").GetProperty("holder").GetString());
            Assert.Equal(HttpStatusCode.NotFound, otherStatus);
        }
        finally
        {
            options.Operators = [];
        }
    }
}
