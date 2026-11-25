using System.Net;
using System.Net.Http.Json;
using System.Text;
using IntoChat.OS.Modules.Identity.Contracts.Accounts;

namespace IntoChat.OS.Tests.Integration.Security.AccessFindings;

// HTTP-edge findings, driven through the harness's real route map and real auth filters (in-process Kestrel on an
// ephemeral loopback port). Each is named for the property that SHOULD hold.
//
// Finding 3 (43-misplaced-subjects.md): /artifacts/* routes were AllowAnonymous, carried no brain in the path, and gated
//   only on IsLoopbackOrAuthenticated. They are gone; a studio page reads and saves workspace files through the hosted
//   storage content route, which is brain-scoped and needs a member's session (StorageContentRouteFacts).
// Finding 4 (same paper): /artcraft/* and /widgetbook/* GET routes are anonymous. The question is whether they can
//   serve anything outside the intended static directory -- i.e. path traversal. This is a GUARD test: the property
//   holds today (ServeStudioAssetAsync bounds the resolved path), so it passes and stays as a regression guard.
// Finding 5 (42-installed-apps.md): DELETE /apps/{name} skips the root-app check that AppStoreNeuron.Remove enforces.
[Trait("Category", "AccessFindings")]
public sealed class AccessFindingsHttpRouteFacts(Gate20EdgeApprovalWallFacts.EdgeKernelFixture fixture)
    : IClassFixture<Gate20EdgeApprovalWallFacts.EdgeKernelFixture>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // Finding 3: with no credential, nothing of a brain's files is read or written, and the old artifact routes are gone.
    [Fact]
    public async Task ReadingOrSavingAStudioDocument_RequiresAnAuthenticatedMember()
    {
        var host = fixture.Host;
        var path = "/work/proof-" + Guid.NewGuid().ToString("N")[..8] + ".txt";

        using var locate = await host.Client.GetAsync($"/brains/{host.Scope}/workspace/item?path={Uri.EscapeDataString(path)}", Ct);
        using var content = await host.Client.GetAsync(DigitalBrain.Runtime.Http.Storage.StorageContentEndpoints.ContentPath(host.Scope, host.Scope + "/local-cloud/main", "ld:x"), Ct);
        using var put = new HttpRequestMessage(HttpMethod.Put, $"/artifacts/bytes?path={Uri.EscapeDataString(path)}&actor=attacker")
        {
            Content = new StringContent("secret-artifact-body", Encoding.UTF8, "text/plain"),
        };
        using var oldWrite = await host.Client.SendAsync(put, Ct);

        Assert.Equal(HttpStatusCode.Unauthorized, locate.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, content.StatusCode);
        Assert.NotEqual(HttpStatusCode.OK, oldWrite.StatusCode);
    }

    // Finding 4: GUARD -- an anonymous artcraft asset request cannot escape the studio's static directory.
    [Theory]
    [InlineData("/artcraft/photocraft/..%2f..%2f..%2fappsettings.json")]
    [InlineData("/artcraft/photocraft/..%5c..%5cweb.config")]
    [InlineData("/artcraft/photocraft/%2e%2e%2f%2e%2e%2fsecrets.json")]
    public async Task AnonymousArtCraftAsset_CannotEscapeTheStudioDirectory(string route)
    {
        var host = fixture.Host;

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
        var host = fixture.Host;

        using var req = new HttpRequestMessage(HttpMethod.Delete, $"/brains/{host.Scope}/apps/{rootApp}");
        req.Headers.Add(PresenceProof.HeaderName, host.PresenceToken);
        using var res = await host.Client.SendAsync(req, Ct);

        // SHOULD hold: the route refuses to uninstall a protected root app. Today it has no root-app check.
        Assert.True(
            res.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.Conflict or HttpStatusCode.BadRequest,
            $"DELETE /apps/{rootApp} was not refused (status {(int)res.StatusCode}).");
    }
}
