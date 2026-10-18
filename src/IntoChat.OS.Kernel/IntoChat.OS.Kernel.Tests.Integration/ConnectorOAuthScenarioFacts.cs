using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Web;

namespace IntoChat.OS.Kernel.Tests.Integration;

// Connecting Gmail the way the shell does: start the browser login, come back through the callback with a code.
public sealed class ConnectorOAuthScenarioFacts(OsHost host)
{
    [Fact]
    public async Task GmailConnectStartRedirectsCallbackExchangesAndStoresTheToken()
    {
        var ct = TestContext.Current.CancellationToken;
        using var ada = await host.PersonAsync("ada", ct);

        using var start = await ada.Http.PostAsJsonAsync($"/brains/{ada.BrainId}/connections/mail/login", new { connectorId = "gmail", authMethodId = "google-oauth" }, ct);
        Assert.Equal(HttpStatusCode.OK, start.StatusCode);
        var redirect = new Uri((await start.Content.ReadFromJsonAsync<JsonElement>(ct)).GetProperty("url").GetString()!);
        var query = HttpUtility.ParseQueryString(redirect.Query);
        Assert.Equal("S256", query["code_challenge_method"]);
        var challenge = query["code_challenge"];
        var state = query["state"];
        Assert.False(string.IsNullOrEmpty(challenge));
        Assert.False(string.IsNullOrEmpty(state));

        using var callback = await ada.Http.GetAsync($"/connections/oauth/gmail/callback?code=fake-code&state={Uri.EscapeDataString(state)}", ct);
        Assert.Equal(HttpStatusCode.OK, callback.StatusCode);

        // The stub is shared by the assembly, so this login's exchange is the one carrying its PKCE verifier.
        var exchange = Assert.Single(host.Tokens.Forms, form => form.TryGetValue("code_verifier", out var verifier) && Challenge(verifier) == challenge);
        Assert.Equal(("authorization_code", "fake-code"), (exchange["grant_type"], exchange["code"]));
        var connections = await ada.Http.GetFromJsonAsync<JsonElement>($"/brains/{ada.BrainId}/connections", ct);
        var gmail = Assert.Single(connections.EnumerateArray());
        Assert.Equal(("Connected", "google-oauth"), (gmail.GetProperty("status").GetString(), gmail.GetProperty("authMethodId").GetString()));
    }

    private static string Challenge(string verifier)
        => Convert.ToBase64String(SHA256.HashData(Encoding.ASCII.GetBytes(verifier))).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
