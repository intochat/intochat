using System.Net;
using System.Net.Http.Json;
using IntoChat.OS.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Time.Testing;

namespace IntoChat.Cloud.Auth.Tests;

public sealed class IpThrottleFacts
{
    [Fact]
    public async Task AnonymousAttempts_AreLimitedPerIp_AndWindowExpiryUnlocks()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        await using var brain = await AuthHost.Compose(TestBrain.Create(), TestLicense.Valid(), ipThrottleLimit: 3)
            .ConfigureServices(services => { services.RemoveAll<TimeProvider>(); services.AddSingleton<TimeProvider>(clock); })
            .StartAsync(TestContext.Current.CancellationToken);
        for (var i = 0; i < 3; i++)
        {
            using var response = await brain.HttpClient.PostAsJsonAsync("/auth/login", new { email = AuthHost.UniqueEmail("ip"), password = "wrong password" }, TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        }
        using var denied = await brain.HttpClient.PostAsJsonAsync("/auth/password-reset", new { email = AuthHost.UniqueEmail("ip") }, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.TooManyRequests, denied.StatusCode);
        clock.Advance(TimeSpan.FromMinutes(11));
        using var allowed = await brain.HttpClient.PostAsJsonAsync("/auth/login", new { email = AuthHost.UniqueEmail("ip"), password = "wrong password" }, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Unauthorized, allowed.StatusCode);
    }
}
