using System.Net;
using System.Net.Http.Json;
using System.Text;
using IntoChat.OS.Modules.Identity.Contracts.Accounts;

namespace IntoChat.OS.Tests.Integration.Security.AccessFindings;

// HTTP-edge findings, driven through the harness's real route map and real auth filters (in-process Kestrel on an
// ephemeral loopback port). Each is named for the property that SHOULD hold.
//
// Finding 3 (43-misplaced-subjects.md): /artifacts/* routes are AllowAnonymous, carry no brain in the path, and gate
//   only on IsLoopbackOrAuthenticated. Over loopback they serve and accept writes with a caller-named actor and no
//   brain scoping.
// Finding 4 (same paper): /artcraft/* and /widgetbook/* GET routes are anonymous. The question is whether they can
//   serve anything outside the intended static directory -- i.e. path traversal. This is a GUARD test: the property
//   holds today (ServeStudioAssetAsync bounds the resolved path), so it passes and stays as a regression guard.
// Finding 5 (42-installed-apps.md): DELETE /apps/{name} skips the root-app check that AppStoreNeuron.Remove enforces.
[Trait("Category", "AccessFindings")]
public sealed class AccessFindingsHttpRouteFacts
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // Finding 3: a live artifact can be written and read with no credential and no brain scope (loopback anonymous).
    [Fact]
    public async Task WritingAndReadingALiveArtifact_RequiresAnAuthenticatedOwner()
    {
        await using var host = await Gate20EdgeApprovalWallFacts.EdgeKernelHarness.StartAsync(Ct);
        var path = "proof-" + Guid.NewGuid().ToString("N")[..8] + ".txt";

        // No presence token at all; caller names its own actor in the query.
        using var put = new HttpRequestMessage(HttpMethod.Put, $"/artifacts/bytes?path={Uri.EscapeDataString(path)}&actor=attacker")
        {
            Content = new StringContent("secret-artifact-body", Encoding.UTF8, "text/plain"),
        };
        using var putRes = await host.Client.SendAsync(put, Ct);

        using var getRes = await host.Client.GetAsync($"/artifacts/bytes?path={Uri.EscapeDataString(path)}", Ct);

        // SHOULD hold: an unauthenticated request cannot write or read a live artifact. If the write was accepted the
        // read returns the body; if the property holds the write is rejected (4xx) and the read is 404/401.
        Assert.False(
            putRes.StatusCode == HttpStatusCode.OK && getRes.StatusCode == HttpStatusCode.OK,
            $"Unauthenticated artifact write+read succeeded (PUT {(int)putRes.StatusCode}, GET {(int)getRes.StatusCode}).");
    }

    // Finding 4: GUARD -- an anonymous artcraft asset request cannot escape the studio's static directory.
    [Theory]
    [InlineData("/artcraft/photocraft/..%2f..%2f..%2fappsettings.json")]
    [InlineData("/artcraft/photocraft/..%5c..%5cweb.config")]
    [InlineData("/artcraft/photocraft/%2e%2e%2f%2e%2e%2fsecrets.json")]
    public async Task AnonymousArtCraftAsset_CannotEscapeTheStudioDirectory(string route)
    {
        await using var host = await Gate20EdgeApprovalWallFacts.EdgeKernelHarness.StartAsync(Ct);

        using var res = await host.Client.GetAsync(route, Ct);

        // SHOULD hold: traversal never yields a 200 with foreign file content. NotFound/BadRequest are acceptable.
        Assert.NotEqual(HttpStatusCode.OK, res.StatusCode);
    }

    // Finding 5: DELETE /apps/{name} must refuse a shipped root app (the check AppStoreNeuron.Remove enforces).
    [Theory]
    [InlineData("settings")]
    [InlineData("ino")]
    [InlineData("creator")]
    public async Task DeletingAppOverHttp_RefusesAShippedRootApp(string rootApp)
    {
        await using var host = await Gate20EdgeApprovalWallFacts.EdgeKernelHarness.StartAsync(Ct);

        using var req = new HttpRequestMessage(HttpMethod.Delete, $"/brains/{host.Scope}/apps/{rootApp}");
        req.Headers.Add(PresenceProof.HeaderName, host.PresenceToken);
        using var res = await host.Client.SendAsync(req, Ct);

        // SHOULD hold: the route refuses to uninstall a protected root app. Today it has no root-app check.
        Assert.True(
            res.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.Conflict or HttpStatusCode.BadRequest,
            $"DELETE /apps/{rootApp} was not refused (status {(int)res.StatusCode}).");
    }
}
