using System.Net;
using System.Net.Http.Json;

namespace DigitalBrain.Authority.Tests.Integration.Identity;

public sealed class SecretOwnershipFacts
{
    [Fact]
    public async Task AnotherOwnersSecretIsForbiddenWhateverThePathSays()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await TestBrain.Create().StartAsync(ct);
        using var alice = await brain.PersonAsync("alice", ct);

        using var response = await alice.Http.PostAsJsonAsync("/secrets/someone-else", new { name = "me.apiKey", label = "API key", value = "canary" }, ct);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }
}
