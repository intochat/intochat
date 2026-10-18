using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace DigitalBrain.OS.Tests.Integration;

// An account's whole life over the identity endpoints, as the shell drives it: register, sign out, sign back in.
public sealed class IdentityScenarioFacts(OsHost host)
{
    // The password Person.RegisterAsync registers every principal with.
    private static string Password(string principal) => principal + "-password-123";

    [Fact]
    public async Task RegisterLoginSessionBrainsLogout()
    {
        var ct = TestContext.Current.CancellationToken;
        using var ada = await host.PersonAsync("ada", ct);

        using (var logout = await ada.Http.PostAsync("/identity/logout", null, ct)) { Assert.Equal(HttpStatusCode.NoContent, logout.StatusCode); }
        using (var signedOut = await ada.Http.GetAsync("/identity/session", ct)) { Assert.Equal(HttpStatusCode.NoContent, signedOut.StatusCode); }
        using (var refused = await ada.Http.GetAsync("/identity/brains", ct)) { Assert.Equal(HttpStatusCode.Unauthorized, refused.StatusCode); }

        using (var login = await ada.Http.PostAsJsonAsync("/identity/login", new { principalId = ada.PrincipalId, password = Password(ada.PrincipalId) }, ct))
        { Assert.Equal(HttpStatusCode.OK, login.StatusCode); }
        var session = await ada.Http.GetFromJsonAsync<JsonElement>("/identity/session", ct);
        Assert.Equal((ada.PrincipalId, ada.AccountId, ada.BrainId),
            (session.GetProperty("principalId").GetString(), session.GetProperty("accountId").GetString(), session.GetProperty("brainId").GetString()));
        var brains = await ada.Http.GetFromJsonAsync<JsonElement>("/identity/brains", ct);
        Assert.Contains(brains.EnumerateArray(), brain => brain.GetProperty("id").GetString() == ada.BrainId);

        using var wrong = await ada.Http.PostAsJsonAsync("/identity/login", new { principalId = ada.PrincipalId, password = "not-the-password" }, ct);
        Assert.Equal(HttpStatusCode.Unauthorized, wrong.StatusCode);
    }

    [Fact]
    public async Task ASecondPersonCannotReadTheFirstPersonsBrains()
    {
        var ct = TestContext.Current.CancellationToken;
        using var ada = await host.PersonAsync("ada", ct);
        using var bob = await host.PersonAsync("bob", ct);

        var brains = await bob.Http.GetFromJsonAsync<JsonElement>("/identity/brains", ct);
        Assert.DoesNotContain(brains.EnumerateArray(), brain => brain.GetProperty("id").GetString() == ada.BrainId);
        using (var connections = await bob.Http.GetAsync($"/brains/{ada.BrainId}/connections", ct))
        { Assert.Equal(HttpStatusCode.Forbidden, connections.StatusCode); }
        using var select = await bob.Http.PostAsJsonAsync("/identity/session/brain", new { accountId = ada.AccountId, brainId = ada.BrainId }, ct);
        Assert.Equal(HttpStatusCode.Forbidden, select.StatusCode);
    }
}
