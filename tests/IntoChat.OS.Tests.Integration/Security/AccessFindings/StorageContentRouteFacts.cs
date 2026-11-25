using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using DigitalBrain.Runtime.Http.Storage;
using IntoChat.OS.Modules.Identity.Accounts;
using IntoChat.OS.Modules.Identity.Contracts.Accounts;
using Microsoft.Extensions.DependencyInjection;

namespace IntoChat.OS.Tests.Integration.Security.AccessFindings;

// The storage content route the product hosts (/brains/{brain}/storage/{segment}/items/...), with the workspace by path
// and the answer to a parked overwrite: what the ArtCraft page and the Files previews use. A file is read and written for
// a person who is a member of the route's brain, through the broker under that person; no session is 401, another brain's
// person is 403, and a save over an existing file waits for the person's answer.
[Trait("Category", "AccessFindings")]
public sealed class StorageContentRouteFacts(StorageContentRouteFacts.Kernel kernel) : IClassFixture<StorageContentRouteFacts.Kernel>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private Gate20EdgeApprovalWallFacts.EdgeKernelHarness Host => kernel.Host;

    private static string NewPath() => "/work/proof-" + Guid.NewGuid().ToString("N")[..8] + ".txt";

    [Fact]
    public async Task TheOwner_SavesAFileToTheWorkspaceAndReadsItBackByTheContentRoute()
    {
        var path = NewPath();
        var before = await Locate(path);
        Assert.Equal(JsonValueKind.Null, before.GetProperty("id").ValueKind);
        Assert.Equal(path, before.GetProperty("path").GetString());

        using var saved = await Upload(before, "the owner's document", planId: null);
        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);

        var after = await Locate(path);
        var content = after.GetProperty("content").GetString()!;
        // The route names the workspace by its connection key in the one encoded form.
        Assert.StartsWith($"/brains/{Host.Scope}/storage/{StorageContentEndpoints.Segment(Host.Scope + "/local-cloud/main")}/items/", content, StringComparison.Ordinal);
        using var read = await Owner(HttpMethod.Get, content);
        Assert.Equal(HttpStatusCode.OK, read.StatusCode);
        Assert.Equal("the owner's document", await read.Content.ReadAsStringAsync(Ct));
        Assert.Equal("nosniff", read.Headers.GetValues("X-Content-Type-Options").Single());
    }

    [Fact]
    public async Task ReplacingAFile_WaitsForThePersonsAnswer()
    {
        var path = NewPath();
        using (var first = await Upload(await Locate(path), "v1", planId: null)) { first.EnsureSuccessStatusCode(); }

        var located = await Locate(path);
        using var conflict = await Upload(located, "v2", planId: null);
        Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
        var planId = (await conflict.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("planId").GetString()!;

        using (var waiting = await Owner(HttpMethod.Post, located.GetProperty("ticket").GetString() + "&planId=" + Uri.EscapeDataString(planId)))
        {
            Assert.Equal(HttpStatusCode.Conflict, waiting.StatusCode);
        }
        using (var answered = await Answer(located, planId, approve: true)) { Assert.Equal(HttpStatusCode.OK, answered.StatusCode); }
        using (var replaced = await Upload(located, "v2", planId)) { Assert.Equal(HttpStatusCode.OK, replaced.StatusCode); }
        Assert.Equal("v2", await Text(path));

        // A refusal leaves the file as it is, and an answered plan takes no second answer.
        using var again = await Upload(await Locate(path), "v3", planId: null);
        var refusedId = (await again.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("planId").GetString()!;
        using (var no = await Answer(located, refusedId, approve: false)) { Assert.Equal(HttpStatusCode.OK, no.StatusCode); }
        using (var refused = await Owner(HttpMethod.Post, located.GetProperty("ticket").GetString() + "&planId=" + Uri.EscapeDataString(refusedId)))
        {
            Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        }
        using (var twice = await Answer(located, refusedId, approve: true)) { Assert.Equal(HttpStatusCode.NotFound, twice.StatusCode); }
        Assert.Equal("v2", await Text(path));
    }

    [Fact]
    public async Task WithNoSession_EveryHostedRouteIsRefused()
    {
        var path = NewPath();
        var located = await Locate(path);
        using (var saved = await Upload(located, "the owner's document", planId: null)) { saved.EnsureSuccessStatusCode(); }
        var content = (await Locate(path)).GetProperty("content").GetString()!;

        foreach (var (method, route) in new[]
        {
            (HttpMethod.Get, content),
            (HttpMethod.Get, $"/brains/{Host.Scope}/workspace/item?path={Uri.EscapeDataString(path)}"),
            (HttpMethod.Post, located.GetProperty("ticket").GetString()!),
            (HttpMethod.Post, located.GetProperty("overwrites").GetString() + "op-none"),
        })
        {
            using var request = new HttpRequestMessage(method, route);
            if (method == HttpMethod.Post) { request.Content = JsonContent.Create(new { approve = true }); }
            using var response = await Host.Client.SendAsync(request, Ct);
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        }
        Assert.Equal("the owner's document", await Text(path));
    }

    [Fact]
    public async Task ASignedInPersonOfAnotherBrain_NeitherReadsNorWritesTheOwnersFiles()
    {
        var path = NewPath();
        var located = await Locate(path);
        using (var saved = await Upload(located, "the owner's document", planId: null)) { saved.EnsureSuccessStatusCode(); }
        var content = (await Locate(path)).GetProperty("content").GetString()!;

        using var stranger = new HttpClient(new HttpClientHandler { UseCookies = true }) { BaseAddress = Host.BaseAddress };
        var principal = "mallory-" + Guid.NewGuid().ToString("N")[..8];
        using var registered = await stranger.PostAsJsonAsync(
            "/identity/register", new { principalId = principal, password = "correct-horse-battery-staple", displayName = principal }, Ct);
        registered.EnsureSuccessStatusCode();

        using (var peek = await stranger.GetAsync(content, Ct)) { Assert.Equal(HttpStatusCode.Forbidden, peek.StatusCode); }
        using (var find = await stranger.GetAsync($"/brains/{Host.Scope}/workspace/item?path={Uri.EscapeDataString(path)}", Ct))
        {
            Assert.Equal(HttpStatusCode.Forbidden, find.StatusCode);
        }
        using (var ticket = await stranger.PostAsync(located.GetProperty("ticket").GetString(), null, Ct))
        {
            Assert.Equal(HttpStatusCode.Forbidden, ticket.StatusCode);
        }
        Assert.Equal("the owner's document", await Text(path));
    }

    // The screens' route (IWorkspaceFiles) and the page's route (the content route) are one workspace: a file a studio
    // screen writes through the neuron on the silo is the file the page reads; another brain's caller reads nothing.
    [Fact]
    public async Task AFileWrittenThroughTheWorkspaceFilesNeuronIsReadByTheContentRoute()
    {
        var path = NewPath();
        var brain = new DigitalBrain.BrainId(Host.Scope);
        var edge = Host.Services.GetRequiredService<DigitalBrain.Access.EdgeAuthority>();
        var files = Host.Services.GetRequiredService<DigitalBrain.Neurons.INeuronProxies>().Get<IntoChat.OS.Modules.Apps.IWorkspaceFiles>(IntoChat.OS.Modules.Apps.WorkspaceFilesDefaults.Id(brain));
        IntoChat.OS.Modules.Apps.WorkspaceFile written;
        // As a studio screen calls it: the app, for the person who uses it.
        var owner = new DigitalBrain.Access.Actor.Person(AccountSession.DefaultLogin, AccountSession.DefaultLogin);
        using (edge.Enter(edge.App(IntoChat.OS.Host.Primitives.AppRef.Of(brain, "data").Key, owner, brain)))
        {
            written = await files.Write(path, Encoding.UTF8.GetBytes("written by a screen"));
            Assert.Equal("written by a screen", Encoding.UTF8.GetString((await files.Read(path))!));
        }
        using (edge.Enter(edge.Person("mallory", "mallory", new DigitalBrain.BrainId("mallory"))))
        {
            await Assert.ThrowsAnyAsync<UnauthorizedAccessException>(() => files.Read(path));
        }
        // An app the workspace files are not granted to reaches nothing.
        using (edge.Enter(edge.App(IntoChat.OS.Host.Primitives.AppRef.Of(brain, "calculator").Key, owner, brain)))
        {
            await Assert.ThrowsAnyAsync<UnauthorizedAccessException>(() => files.Read(path));
        }

        Assert.Equal(path, written.Path);
        Assert.Equal("written by a screen", await Text(path));
    }

    [Fact]
    public async Task APathOutsideTheWorkspaceIsNotLocated()
    {
        using var other = await Owner(HttpMethod.Get, $"/brains/{Host.Scope}/workspace/item?path={Uri.EscapeDataString("/local/secret.txt")}");
        Assert.Equal(HttpStatusCode.BadRequest, other.StatusCode);
        using var climb = await Owner(HttpMethod.Get, $"/brains/{Host.Scope}/workspace/item?path={Uri.EscapeDataString("../outside.txt")}");
        Assert.Equal(HttpStatusCode.BadRequest, climb.StatusCode);
        using var missing = await Owner(HttpMethod.Get, $"/brains/{Host.Scope}/workspace/item?path={Uri.EscapeDataString("/work/no-such-folder/a.txt")}");
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
    }

    private async Task<JsonElement> Locate(string path)
    {
        using var response = await Owner(HttpMethod.Get, $"/brains/{Host.Scope}/workspace/item?path={Uri.EscapeDataString(path)}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
    }

    private async Task<string> Text(string path)
    {
        using var read = await Owner(HttpMethod.Get, (await Locate(path)).GetProperty("content").GetString()!);
        read.EnsureSuccessStatusCode();
        return await read.Content.ReadAsStringAsync(Ct);
    }

    // The page's save: a ticket for the folder and name (bound to an answered overwrite when there is one), then the bytes.
    private async Task<HttpResponseMessage> Upload(JsonElement located, string body, string? planId)
    {
        var ticketRoute = located.GetProperty("ticket").GetString() + (planId is null ? "" : "&planId=" + Uri.EscapeDataString(planId));
        using var ticket = await Owner(HttpMethod.Post, ticketRoute);
        Assert.Equal(HttpStatusCode.OK, ticket.StatusCode);
        var url = (await ticket.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("url").GetString()!;
        using var put = new HttpRequestMessage(HttpMethod.Put, url) { Content = new StringContent(body, Encoding.UTF8, "text/plain") };
        put.Headers.Add(PresenceProof.HeaderName, Host.PresenceToken);
        put.Headers.Add("X-Upload", "1");
        return await Host.Client.SendAsync(put, Ct);
    }

    private async Task<HttpResponseMessage> Answer(JsonElement located, string planId, bool approve)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, located.GetProperty("overwrites").GetString() + Uri.EscapeDataString(planId))
        {
            Content = JsonContent.Create(new { approve }),
        };
        request.Headers.Add(PresenceProof.HeaderName, Host.PresenceToken);
        return await Host.Client.SendAsync(request, Ct);
    }

    private async Task<HttpResponseMessage> Owner(HttpMethod method, string route)
    {
        using var request = new HttpRequestMessage(method, route);
        request.Headers.Add(PresenceProof.HeaderName, Host.PresenceToken);
        return await Host.Client.SendAsync(request, Ct);
    }

    public sealed class Kernel : IAsyncLifetime
    {
        internal Gate20EdgeApprovalWallFacts.EdgeKernelHarness Host { get; private set; } = null!;

        public async ValueTask InitializeAsync() => Host = await Gate20EdgeApprovalWallFacts.EdgeKernelHarness.StartAsync(CancellationToken.None);

        public ValueTask DisposeAsync() => Host.DisposeAsync();
    }
}
