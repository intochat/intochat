using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using DigitalBrain;
using DigitalBrain.Access;
using DigitalBrain.Capabilities;
using DigitalBrain.Runtime.Orleans.Hosting;
using IntoChat.OS.Authority.Identity;
using IntoChat.OS.Modules.Apps;
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
            $"/apps/node?kind=view&name={Uri.EscapeDataString(IntoChat.OS.Primitives.AppRef.Of(new BrainId(harness.Scope), "desktop").ViewKey)}");
        Assert.Equal(HttpStatusCode.OK, desktopViewResponse.Status);

        using var initialViewDoc = JsonDocument.Parse(desktopViewResponse.Body);
        var initialGen = initialViewDoc.RootElement.GetProperty("generation").GetInt64();
        Assert.Contains("dock-files", desktopViewResponse.Body, StringComparison.Ordinal);

        // Activate "dock-files" via real AppUiEndpoints -> ScreenAppEmitBridge -> DesktopApp -> real WorkspaceNeuron.Apply.
        var eventResponse = await SendAsync(
            harness,
            HttpMethod.Post,
            "/apps/event",
            new
            {
                kind = "view",
                name = IntoChat.OS.Primitives.AppRef.Of(new BrainId(harness.Scope), "desktop").ViewKey,
                key = "dock-files",
                generation = initialGen,
            });
        Assert.Equal(HttpStatusCode.OK, eventResponse.Status);

        var filesAppKey = IntoChat.OS.Primitives.AppRef.Of(new BrainId(harness.Scope), "files").Key;
        var wsResponse = await SendAsync(harness, HttpMethod.Get, "");
        Assert.Equal(HttpStatusCode.OK, wsResponse.Status);

        var ws = JsonSerializer.Deserialize<WorkspaceState>(wsResponse.Body, JsonSerializerOptions.Web);
        Assert.NotNull(ws);
        Assert.Contains(ws!.OpenViews, v => v.App == filesAppKey);
        Assert.Equal("files", ws.FocusedViewId);
        Assert.Contains(ws.Windows, w => w.ActiveTabId == "files" && w.Tabs.Any(t => t.App == filesAppKey));
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
            [new WorkspaceViewRef("files", IntoChat.OS.Primitives.AppRef.Of(brainId, "files").Key, "full", "")],
            "files",
            []);

        // 1. Direct human person caller cannot call IWorkspace.Apply directly.
        using (edge.Enter(edge.Person(AccountSession.DefaultLogin, AccountSession.DefaultLogin, brainId)))
        {
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
                workspace.Apply(update, "direct-person-op", current.Revision));
        }

        // 2. Non-desktop app caller cannot call IWorkspace.Apply.
        var foreignAppKey = IntoChat.OS.Primitives.AppRef.Of(brainId, "calculator").Key;
        using (edge.Enter(edge.App(foreignAppKey, new Actor.Person(AppPrincipal.Id(foreignAppKey), AccountSession.DefaultLogin), brainId)))
        {
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
                workspace.Apply(update, "foreign-app-op", current.Revision));
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
