using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using IntoChat.OS.Modules.Identity.Contracts.Accounts;

namespace IntoChat.OS.Tests.Integration.Security.AccessFindings;

// /artifacts/* serves the live documents a studio page shares with Ino. Every read and write is for the brain of the
// stamped caller: no session, no artifact; another brain's person reads and writes only their own.
[Trait("Category", "AccessFindings")]
public sealed class ArtifactScopeFacts(ArtifactScopeFacts.Kernel kernel) : IClassFixture<ArtifactScopeFacts.Kernel>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private Gate20EdgeApprovalWallFacts.EdgeKernelHarness Host => kernel.Host;

    private static string NewPath() => "work/proof-" + Guid.NewGuid().ToString("N")[..8] + ".txt";

    [Theory]
    [InlineData("GET", "/artifacts/meta")]
    [InlineData("GET", "/artifacts/bytes")]
    [InlineData("PUT", "/artifacts/bytes")]
    [InlineData("GET", "/artifacts/stream")]
    public async Task WithNoSession_EveryArtifactRouteIsRefused(string method, string route)
    {
        var path = NewPath();
        using var seeded = await Owner(HttpMethod.Put, "/artifacts/bytes", path, "the owner's document");
        seeded.EnsureSuccessStatusCode();

        using var request = new HttpRequestMessage(new HttpMethod(method), $"{route}?path={Uri.EscapeDataString(path)}&actor=attacker");
        if (method == "PUT") { request.Content = new StringContent("overwritten", Encoding.UTF8, "text/plain"); }
        using var response = await Host.Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, Ct);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        using var still = await Owner(HttpMethod.Get, "/artifacts/bytes", path);
        Assert.Equal("the owner's document", await still.Content.ReadAsStringAsync(Ct));
    }

    [Fact]
    public async Task TheOwningBrain_WritesReadsAndFollowsItsArtifact()
    {
        var path = NewPath();

        // The studio page's own calls: the session cookie or presence proof of the person, the actor label in the query.
        using var written = await Owner(HttpMethod.Put, "/artifacts/bytes", path, "v1", actor: "ino");
        Assert.Equal(HttpStatusCode.OK, written.StatusCode);
        var meta = await written.Content.ReadFromJsonAsync<JsonElement>(Ct);
        Assert.Equal(1, meta.GetProperty("version").GetInt32());
        // Who wrote it is the stamped caller, a person, whatever the query says.
        Assert.Equal("user", meta.GetProperty("updatedBy").GetString());

        using var read = await Owner(HttpMethod.Get, "/artifacts/bytes", path);
        Assert.Equal(HttpStatusCode.OK, read.StatusCode);
        Assert.Equal("v1", await read.Content.ReadAsStringAsync(Ct));

        using var described = await Owner(HttpMethod.Get, "/artifacts/meta", path);
        Assert.Equal(HttpStatusCode.OK, described.StatusCode);

        using var stream = await Owner(HttpMethod.Get, "/artifacts/stream", path, headersOnly: true);
        Assert.Equal(HttpStatusCode.OK, stream.StatusCode);
        Assert.Equal("text/event-stream", stream.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task ASignedInPersonOfAnotherBrain_NeitherReadsNorOverwritesIt()
    {
        var path = NewPath();
        using var seeded = await Owner(HttpMethod.Put, "/artifacts/bytes", path, "the owner's document");
        seeded.EnsureSuccessStatusCode();

        using var stranger = new HttpClient(new HttpClientHandler { UseCookies = true }) { BaseAddress = Host.BaseAddress };
        var principal = "mallory-" + Guid.NewGuid().ToString("N")[..8];
        using var registered = await stranger.PostAsJsonAsync(
            "/identity/register", new { principalId = principal, password = "correct-horse-battery-staple", displayName = principal }, Ct);
        registered.EnsureSuccessStatusCode();
        var route = "/artifacts/bytes?path=" + Uri.EscapeDataString(path);

        using var peek = await stranger.GetAsync(route, Ct);
        Assert.Equal(HttpStatusCode.NotFound, peek.StatusCode);
        using var meta = await stranger.GetAsync("/artifacts/meta?path=" + Uri.EscapeDataString(path), Ct);
        Assert.Equal(HttpStatusCode.NotFound, meta.StatusCode);

        // The same path in the stranger's own brain is the stranger's own artifact.
        using var theirs = await stranger.PutAsync(route, new StringContent("mallory's document", Encoding.UTF8, "text/plain"), Ct);
        Assert.Equal(HttpStatusCode.OK, theirs.StatusCode);
        using var own = await stranger.GetAsync(route, Ct);
        Assert.Equal("mallory's document", await own.Content.ReadAsStringAsync(Ct));

        using var still = await Owner(HttpMethod.Get, "/artifacts/bytes", path);
        Assert.Equal("the owner's document", await still.Content.ReadAsStringAsync(Ct));
    }

    private async Task<HttpResponseMessage> Owner(HttpMethod method, string route, string path, string? body = null, string? actor = null, bool headersOnly = false)
    {
        using var request = new HttpRequestMessage(method, $"{route}?path={Uri.EscapeDataString(path)}" + (actor is null ? "" : "&actor=" + actor));
        request.Headers.Add(PresenceProof.HeaderName, Host.PresenceToken);
        if (body is not null) { request.Content = new StringContent(body, Encoding.UTF8, "text/plain"); }
        return await Host.Client.SendAsync(request, headersOnly ? HttpCompletionOption.ResponseHeadersRead : HttpCompletionOption.ResponseContentRead, Ct);
    }

    public sealed class Kernel : IAsyncLifetime
    {
        internal Gate20EdgeApprovalWallFacts.EdgeKernelHarness Host { get; private set; } = null!;

        public async ValueTask InitializeAsync() => Host = await Gate20EdgeApprovalWallFacts.EdgeKernelHarness.StartAsync(CancellationToken.None);

        public ValueTask DisposeAsync() => Host.DisposeAsync();
    }
}
