using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using DigitalBrain;
using DigitalBrain.Access;
using DigitalBrain.Capabilities;
using DigitalBrain.Runtime.Orleans.Hosting;
using IntoChat.OS.Authority.Identity;
using IntoChat.OS.Modules.Apps;
using IntoChat.OS.Modules.Flutter;
using IntoChat.OS.Modules.Flutter.Viewport;
using IntoChat.OS.Modules.Flutter.Workspace;
using Microsoft.Extensions.DependencyInjection;
using EdgeKernelHarness = IntoChat.OS.Tests.Integration.Security.Gate20EdgeApprovalWallFacts.EdgeKernelHarness;

namespace IntoChat.OS.Tests.Integration.Security;

public sealed class DesktopWorkspaceAuthorityFacts
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task DesktopUiEventActivation_AppliesWorkspaceStateToRealWorkspaceNeuron()
    {
        await using var harness = await EdgeKernelHarness.StartAsync(Ct);

        // Wait until shipped desktop app is installed and connect UI root.
        await EventuallyAsync(
            () => SendAsync(harness, HttpMethod.Get, "/apps/desktop"),
            r => r.Status == HttpStatusCode.OK,
            "Desktop app was never installed.");

        var connect = await SendAsync(harness, HttpMethod.Post, "/apps/connect", new { });
        Assert.Equal(HttpStatusCode.OK, connect.Status);

        var desktopViewResponse = await SendAsync(
            harness,
            HttpMethod.Get,
            $"/apps/node?kind=view&name={Uri.EscapeDataString(IntoChat.OS.Host.Primitives.AppRef.Of(new BrainId(harness.Scope), "desktop").ViewKey)}");
        Assert.Equal(HttpStatusCode.OK, desktopViewResponse.Status);

        using var initialViewDoc = JsonDocument.Parse(desktopViewResponse.Body);
        var initialGen = initialViewDoc.RootElement.GetProperty("generation").GetInt64();
        Assert.Contains("dock-ino", desktopViewResponse.Body, StringComparison.Ordinal);
        Assert.Contains("dock-files", desktopViewResponse.Body, StringComparison.Ordinal);

        // 1. Activate "dock-ino" -> opens Ino in real WorkspaceNeuron AND populates real ViewportNeuron.
        var desktopViewKey = IntoChat.OS.Host.Primitives.AppRef.Of(new BrainId(harness.Scope), "desktop").ViewKey;
        var inoEventResponse = await SendAsync(
            harness,
            HttpMethod.Post,
            "/apps/event",
            new
            {
                kind = "view",
                name = desktopViewKey,
                key = "dock-ino",
                generation = initialGen,
            });
        Assert.Equal(HttpStatusCode.OK, inoEventResponse.Status);

        var inoAppKey = IntoChat.OS.Host.Primitives.AppRef.Of(new BrainId(harness.Scope), "ino").Key;
        var inoWsResponse = await SendAsync(harness, HttpMethod.Get, "");
        Assert.Equal(HttpStatusCode.OK, inoWsResponse.Status);
        var inoWs = JsonSerializer.Deserialize<WorkspaceState>(inoWsResponse.Body, JsonSerializerOptions.Web);
        Assert.NotNull(inoWs);
        var inoWindow = Assert.Single(inoWs!.Windows, w => w.ActiveTabId == "ino" && w.Tabs.Any(t => t.App == inoAppKey));

        var inoViewportKey = IntoChat.OS.Host.Primitives.AppRef.Of(new BrainId(harness.Scope), "desktop").ChildKey($"window/{inoWindow.Id}");
        var inoViewportResponse = await SendAsync(
            harness,
            HttpMethod.Get,
            $"/apps/node?kind=viewport&name={Uri.EscapeDataString(inoViewportKey)}");
        Assert.Equal(HttpStatusCode.OK, inoViewportResponse.Status);
        using (var inoVpDoc = JsonDocument.Parse(inoViewportResponse.Body))
        {
            Assert.True(
                inoVpDoc.RootElement.TryGetProperty("root", out var rootElem) && rootElem.ValueKind == JsonValueKind.Object,
                $"Expected live viewport root for Ino window, got: {inoViewportResponse.Body}");
            Assert.Equal("view", rootElem.GetProperty("kind").GetString());
            Assert.Equal(
                IntoChat.OS.Host.Primitives.AppRef.Of(new BrainId(harness.Scope), "ino").ViewKey,
                rootElem.GetProperty("name").GetString());
        }

        // 2. Read updated desktop view generation and activate "dock-files" via real AppUiEndpoints -> DesktopApp -> real WorkspaceNeuron + ViewportNeuron.
        var refreshedDesktopView = await SendAsync(
            harness,
            HttpMethod.Get,
            $"/apps/node?kind=view&name={Uri.EscapeDataString(desktopViewKey)}");
        Assert.Equal(HttpStatusCode.OK, refreshedDesktopView.Status);
        using var refreshedDoc = JsonDocument.Parse(refreshedDesktopView.Body);
        var refreshedGen = refreshedDoc.RootElement.GetProperty("generation").GetInt64();

        var eventResponse = await SendAsync(
            harness,
            HttpMethod.Post,
            "/apps/event",
            new
            {
                kind = "view",
                name = desktopViewKey,
                key = "dock-files",
                generation = refreshedGen,
            });
        Assert.Equal(HttpStatusCode.OK, eventResponse.Status);

        var filesAppKey = IntoChat.OS.Host.Primitives.AppRef.Of(new BrainId(harness.Scope), "files").Key;
        var wsResponse = await SendAsync(harness, HttpMethod.Get, "");
        Assert.Equal(HttpStatusCode.OK, wsResponse.Status);

        var ws = JsonSerializer.Deserialize<WorkspaceState>(wsResponse.Body, JsonSerializerOptions.Web);
        Assert.NotNull(ws);
        Assert.Contains(ws!.OpenViews, v => v.App == filesAppKey);
        Assert.Equal("files", ws.FocusedViewId);
        var filesWindow = Assert.Single(ws.Windows, w => w.ActiveTabId == "files" && w.Tabs.Any(t => t.App == filesAppKey));

        var filesViewportKey = IntoChat.OS.Host.Primitives.AppRef.Of(new BrainId(harness.Scope), "desktop").ChildKey($"window/{filesWindow.Id}");
        var filesViewportResponse = await SendAsync(
            harness,
            HttpMethod.Get,
            $"/apps/node?kind=viewport&name={Uri.EscapeDataString(filesViewportKey)}");
        Assert.Equal(HttpStatusCode.OK, filesViewportResponse.Status);
        using (var filesVpDoc = JsonDocument.Parse(filesViewportResponse.Body))
        {
            Assert.True(
                filesVpDoc.RootElement.TryGetProperty("root", out var rootElem) && rootElem.ValueKind == JsonValueKind.Object,
                $"Expected live viewport root for Files window, got: {filesViewportResponse.Body}");
            Assert.Equal("view", rootElem.GetProperty("kind").GetString());
            Assert.Equal(
                IntoChat.OS.Host.Primitives.AppRef.Of(new BrainId(harness.Scope), "files").ViewKey,
                rootElem.GetProperty("name").GetString());
        }

        // 3. Closing the Files window clears its ViewportNeuron target back to null.
        var postFilesDesktopView = await SendAsync(
            harness,
            HttpMethod.Get,
            $"/apps/node?kind=view&name={Uri.EscapeDataString(desktopViewKey)}");
        Assert.Equal(HttpStatusCode.OK, postFilesDesktopView.Status);
        using var postFilesDoc = JsonDocument.Parse(postFilesDesktopView.Body);
        var closeGen = postFilesDoc.RootElement.GetProperty("generation").GetInt64();
        var closeResponse = await SendAsync(
            harness,
            HttpMethod.Post,
            "/apps/event",
            new
            {
                kind = "view",
                name = desktopViewKey,
                key = $"close-{filesWindow.Id}",
                generation = closeGen,
            });
        Assert.Equal(HttpStatusCode.OK, closeResponse.Status);

        var closedViewportResponse = await SendAsync(
            harness,
            HttpMethod.Get,
            $"/apps/node?kind=viewport&name={Uri.EscapeDataString(filesViewportKey)}");
        Assert.Equal(HttpStatusCode.OK, closedViewportResponse.Status);
        using var closedVpDoc = JsonDocument.Parse(closedViewportResponse.Body);
        Assert.True(
            !closedVpDoc.RootElement.TryGetProperty("root", out var closedRoot) || closedRoot.ValueKind == JsonValueKind.Null,
            $"Expected cleared viewport root after close, got: {closedViewportResponse.Body}");
    }

    [Fact]
    public async Task DirectAndForeignAppWorkspaceApplyCalls_AreStillRefusedByRealWorkspaceNeuron()
    {
        await using var harness = await EdgeKernelHarness.StartAsync(Ct);

        await EventuallyAsync(
            () => SendAsync(harness, HttpMethod.Get, "/apps/desktop"),
            r => r.Status == HttpStatusCode.OK,
            "Desktop app was never installed.");

        var brainId = new BrainId(harness.Scope);
        var edge = harness.Services.GetRequiredService<EdgeAuthority>();
        var workspace = SiloNeurons.Get<IWorkspace>(harness.Services, IWorkspace.Key(harness.Scope));
        WorkspaceState current;
        using (edge.Enter(edge.Person(AccountSession.DefaultLogin, AccountSession.DefaultLogin, brainId)))
        {
            current = await workspace.Read();
        }

        var update = new WorkspaceUpdate(
            [new WorkspaceViewRef("files", IntoChat.OS.Host.Primitives.AppRef.Of(brainId, "files").Key, "full", "")],
            "files",
            []);

        // 1. Direct human person caller cannot call IWorkspace.Apply directly.
        using (edge.Enter(edge.Person(AccountSession.DefaultLogin, AccountSession.DefaultLogin, brainId)))
        {
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
                workspace.Apply(update, "direct-person-op", current.Revision));
        }

        // 2. Non-desktop app caller cannot call IWorkspace.Apply.
        var foreignAppKey = IntoChat.OS.Host.Primitives.AppRef.Of(brainId, "calculator").Key;
        var desktopViewport = SiloNeurons.Get<IViewport>(
            harness.Services,
            IntoChat.OS.Host.Primitives.AppRef.Of(brainId, "desktop").ChildNeuron(UIVocabulary.ViewportType, "window/w-1"));
        ViewportState currentVp;
        using (edge.Enter(edge.Person(AccountSession.DefaultLogin, AccountSession.DefaultLogin, brainId)))
        {
            currentVp = await desktopViewport.Read();
        }
        using (edge.Enter(edge.App(foreignAppKey, new Actor.Person(AppPrincipal.Id(foreignAppKey), AccountSession.DefaultLogin), brainId)))
        {
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
                workspace.Apply(update, "foreign-app-op", current.Revision));
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
                desktopViewport.Set(new WorkspaceViewRef("hijack", foreignAppKey, "full", ""), currentVp.Revision));
        }

        // 3. Cross-brain caller cannot mutate another brain's desktop viewport.
        var foreignBrain = new BrainId("other-brain");
        using (edge.Enter(edge.Person(AccountSession.DefaultLogin, AccountSession.DefaultLogin, foreignBrain)))
        {
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
                desktopViewport.Set(new WorkspaceViewRef("hijack", foreignAppKey, "full", ""), currentVp.Revision));
        }
    }

    private static async Task<(HttpStatusCode Status, string Body)> SendAsync(
        EdgeKernelHarness harness,
        HttpMethod method,
        string relativeBrainRoute,
        object? body = null)
    {
        var path = $"/brains/{harness.Scope}{relativeBrainRoute}";
        using var request = new HttpRequestMessage(method, path);
        request.Headers.Add(PresenceProofValidator.HeaderName, harness.PresenceToken);
        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }

        using var response = await harness.Client.SendAsync(request, Ct);
        return (response.StatusCode, await response.Content.ReadAsStringAsync(Ct));
    }

    private static async Task<(HttpStatusCode Status, string Body)> EventuallyAsync(
        Func<Task<(HttpStatusCode Status, string Body)>> probe,
        Func<(HttpStatusCode Status, string Body), bool> done,
        string failureMessage)
    {
        var latest = await probe();
        for (var i = 0; i < 400 && !done(latest); i++)
        {
            await Task.Delay(50, Ct);
            latest = await probe();
        }

        Assert.True(done(latest), $"{failureMessage} Last: {latest.Status} {latest.Body}");
        return latest;
    }
}
