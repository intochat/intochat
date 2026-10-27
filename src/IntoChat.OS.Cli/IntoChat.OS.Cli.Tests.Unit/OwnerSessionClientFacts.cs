using System.Net;
using System.Text.Json;

namespace IntoChat.OS.Cli.Tests.Unit;

public sealed class OwnerSessionClientFacts
{
    [Fact]
    public async Task AmbiguousDestinationDoesNotInstall()
    {
        var handler = new Handler(request => request.RequestUri!.AbsolutePath switch
        {
            "/identity/session" => Json(new { principalId = "owner", accountId = "account", brainId = "home", role = "owner" }),
            "/identity/brains" => Json(new[] { new { id = "work", kind = "workspace" }, new { id = "work", kind = "workspace" } }),
            _ => throw new InvalidOperationException("Must not install or select an ambiguous destination"),
        });
        var client = new OwnerSessionClient(new HttpClient(handler) { BaseAddress = new("http://localhost:5080/") });
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.Connect("work", TestContext.Current.CancellationToken));
        Assert.Equal(2, handler.Count);
    }

    [Fact]
    public async Task ExistingAppNeedsReplacementConsent()
    {
        var activated = false;
        var handler = new Handler(request => request.RequestUri!.AbsolutePath switch
        {
            "/identity/session" => Json(new { principalId = "owner", accountId = "account", brainId = "home", role = "owner" }),
            "/brains/home/apps/packages" => Json(new { digest = "digest", id = "counter", name = "Counter", version = "1", requires = new { }, hostAdapters = Array.Empty<string>(), trustMessage = "Trusted host code" }),
            "/brains/home/apps/packages/review" => Json(new { token = "review", digest = "digest", brain = "home", name = "counter", expectedRevision = 2, replacesExisting = true, expiresAt = DateTimeOffset.UtcNow.AddMinutes(10), requires = new { }, hostAdapters = Array.Empty<string>() }),
            _ => Activate(),
        });
        HttpResponseMessage Activate() { activated = true; return Json(new { }); }
        var client = new OwnerSessionClient(new HttpClient(handler) { BaseAddress = new("http://localhost:5080/") });
        await client.Connect(null, TestContext.Current.CancellationToken);
        var output = new StringWriter();
        var commands = new AppCommands(client, new StringReader("no\n"), output, true);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => commands.Install(new MemoryStream([1]), TestContext.Current.CancellationToken));
        Assert.False(activated);
        Assert.Contains("Replaces existing app: True", output.ToString(), StringComparison.Ordinal);
        Assert.Contains("SHA256: digest", output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExactApprovalPayloadFollowsActualInteractiveConsent()
    {
        string? approval = null;
        var handler = new Handler(request =>
        {
            if (request.RequestUri!.AbsolutePath == "/identity/session") { return Json(new { principalId = "owner", accountId = "account", brainId = "home", role = "owner" }); }
            if (request.RequestUri.AbsolutePath.EndsWith("/activate", StringComparison.Ordinal))
            {
                approval = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
                return Json(new { activeCompiled = new { digest = "digest", revision = 3, operationId = Guid.NewGuid() } });
            }
            if (request.RequestUri.AbsolutePath.EndsWith("/review", StringComparison.Ordinal))
            { return Json(new { token = "review", digest = "digest", brain = "home", name = "counter", expectedRevision = 2, replacesExisting = true, expiresAt = DateTimeOffset.UtcNow.AddMinutes(10), requires = new { capabilities = new[] { "storage@1" } }, hostAdapters = new[] { "storage" } }); }
            Assert.Equal("application/octet-stream", request.Content!.Headers.ContentType!.MediaType);
            return Json(new { digest = "digest", id = "counter", name = "Counter", version = "1", requires = new { capabilities = new[] { "storage@1" } }, hostAdapters = new[] { "storage" }, trustMessage = "Trusted code" });
        });
        var client = new OwnerSessionClient(new HttpClient(handler) { BaseAddress = new("http://localhost:5080/") });
        await client.Connect(null, TestContext.Current.CancellationToken);
        var result = await new AppCommands(client, new StringReader("INSTALL\n"), new StringWriter(), true).Install(new MemoryStream([1]), TestContext.Current.CancellationToken);
        using var json = JsonDocument.Parse(approval!);
        var approved = json.RootElement.GetProperty("approval");
        Assert.True(approved.GetProperty("trustCode").GetBoolean());
        Assert.True(approved.GetProperty("replaceExisting").GetBoolean());
        Assert.Equal("review", approved.GetProperty("reviewToken").GetString());
        Assert.Equal(new OwnerSessionClient.Requirements(Capabilities: ["storage@1"]).Consent(), approved.GetProperty("requirementsConsent").GetString());
        Assert.Equal(3, result.ActiveCompiled!.Revision);
    }

    [Fact]
    public async Task EditDuringOwnerReviewCannotActivateStaleCandidate()
    {
        var activated = false;
        var current = true;
        var handler = new Handler(request => request.RequestUri!.AbsolutePath switch
        {
            "/identity/session" => Json(new { principalId = "owner", accountId = "account", brainId = "home", role = "owner" }),
            "/brains/home/apps/packages" => Json(new { digest = "digest", id = "counter", name = "Counter", version = "1", requires = new { }, hostAdapters = Array.Empty<string>(), trustMessage = "Trusted host code" }),
            "/brains/home/apps/packages/review" => Json(new { token = "review", digest = "digest", brain = "home", name = "counter", expectedRevision = 2, replacesExisting = true, expiresAt = DateTimeOffset.UtcNow.AddMinutes(10), requires = new { }, hostAdapters = Array.Empty<string>() }),
            _ => Activate(),
        });
        HttpResponseMessage Activate() { activated = true; return Json(new { activeCompiled = new { digest = "digest", revision = 3, operationId = Guid.NewGuid() } }); }
        var client = new OwnerSessionClient(new HttpClient(handler) { BaseAddress = new("http://localhost:5080/") });
        await client.Connect(null, TestContext.Current.CancellationToken);
        var commands = new AppCommands(client, new EditingReader(() => current = false), new StringWriter(), true);
        await Assert.ThrowsAnyAsync<Exception>(() => commands.Install(new MemoryStream([1]), TestContext.Current.CancellationToken, () => current));
        Assert.False(activated);
    }

    [Fact]
    public async Task RedirectedInputCannotApproveCodeEvenWhenItContainsInstall()
    {
        var activated = false;
        var handler = new Handler(request => request.RequestUri!.AbsolutePath switch
        {
            "/identity/session" => Json(new { principalId = "owner", accountId = "account", brainId = "home", role = "owner" }),
            "/brains/home/apps/packages" => Json(new { digest = "digest", id = "counter", name = "Counter", version = "1", requires = new { }, hostAdapters = Array.Empty<string>(), trustMessage = "Trusted host code" }),
            "/brains/home/apps/packages/review" => Json(new { token = "review", digest = "digest", brain = "home", name = "counter", expectedRevision = 2, replacesExisting = false, expiresAt = DateTimeOffset.UtcNow.AddMinutes(10), requires = new { }, hostAdapters = Array.Empty<string>() }),
            _ => Activate(),
        });
        HttpResponseMessage Activate() { activated = true; return Json(new { }); }
        var client = new OwnerSessionClient(new HttpClient(handler) { BaseAddress = new("http://localhost:5080/") });
        await client.Connect(null, TestContext.Current.CancellationToken);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => new AppCommands(client, new StringReader("INSTALL\n"), new StringWriter(), false).Install(new MemoryStream([1]), TestContext.Current.CancellationToken));
        Assert.False(activated);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BrainSelectionUsesExactOwnerAndVerifiesReturnedSession(bool alteredOwner)
    {
        var sessions = 0;
        string? selection = null;
        var handler = new Handler(request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path == "/identity/session")
            {
                sessions++;
                return Json(new { principalId = "owner", accountId = sessions > 1 && alteredOwner ? "other" : "account", brainId = sessions > 1 ? "work" : "home", role = "owner" });
            }
            if (path == "/identity/brains") { return Json(new[] { new { id = "work", kind = "workspace" } }); }
            Assert.Equal("/identity/session/brain", path);
            selection = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            return Json(new { });
        });
        var http = new HttpClient(handler) { BaseAddress = new("http://localhost:5080/") };
        http.DefaultRequestHeaders.Authorization = new("Bearer", "intentional-test-secret");
        var client = new OwnerSessionClient(http);
        if (alteredOwner) { await Assert.ThrowsAsync<UnauthorizedAccessException>(() => client.Connect("work", TestContext.Current.CancellationToken)); }
        else { await client.Connect("work", TestContext.Current.CancellationToken); Assert.Equal("work", client.Brain); }
        using var json = JsonDocument.Parse(selection!);
        Assert.Equal("account", json.RootElement.GetProperty("accountId").GetString());
        Assert.Equal("work", json.RootElement.GetProperty("brainId").GetString());
        Assert.DoesNotContain("intentional-test-secret", selection!, StringComparison.Ordinal);
        Assert.Equal(4, handler.Count);
    }
    private sealed class EditingReader(Action edit) : StringReader("")
    {
        public override ValueTask<string?> ReadLineAsync(CancellationToken cancellationToken) { edit(); return ValueTask.FromResult<string?>("INSTALL"); }
    }
    private static HttpResponseMessage Json(object body) => new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(body), System.Text.Encoding.UTF8, "application/json") };
    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        internal int Count { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) { Count++; return Task.FromResult(respond(request)); }
    }
}
