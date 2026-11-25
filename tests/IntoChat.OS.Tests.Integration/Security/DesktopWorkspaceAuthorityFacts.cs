using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using DigitalBrain;
using DigitalBrain.Access;
using DigitalBrain.Capabilities;
using DigitalBrain.Runtime.Orleans.Hosting;
using IntoChat.OS.Modules.Apps;
using IntoChat.OS.Modules.Flutter;
using IntoChat.OS.Modules.Flutter.Viewport;
using IntoChat.OS.Modules.Flutter.Workspace;
using Microsoft.Extensions.DependencyInjection;
using EdgeKernelHarness = IntoChat.OS.Tests.Integration.Security.Gate20EdgeApprovalWallFacts.EdgeKernelHarness;
using IntoChat.OS.Modules.Identity.Accounts;
using IntoChat.OS.Modules.Identity.Contracts.Accounts;

namespace IntoChat.OS.Tests.Integration.Security;

public sealed class DesktopWorkspaceAuthorityFacts(Gate20EdgeApprovalWallFacts.EdgeKernelFixture fixture)
    : IClassFixture<Gate20EdgeApprovalWallFacts.EdgeKernelFixture>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task DesktopPressesOverAct_ApplyWorkspaceStateToRealWorkspaceNeuron()
    {
        var harness = fixture.Host;

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

        Assert.Contains("dock-ino", desktopViewResponse.Body, StringComparison.Ordinal);
        Assert.Contains("dock-files", desktopViewResponse.Body, StringComparison.Ordinal);

        // 1. Press "dock-ino" -> opens Ino in real WorkspaceNeuron AND populates real ViewportNeuron.
        var desktopViewKey = IntoChat.OS.Host.Primitives.AppRef.Of(new BrainId(harness.Scope), "desktop").ViewKey;
        var inoEventResponse = await PressAsync(harness, desktopViewResponse.Body, "dock-ino");
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

        // 2. Read the updated desktop view and press "dock-files" via real AppUiEndpoints -> DesktopApp -> real WorkspaceNeuron + ViewportNeuron.
        var refreshedDesktopView = await SendAsync(
            harness,
            HttpMethod.Get,
            $"/apps/node?kind=view&name={Uri.EscapeDataString(desktopViewKey)}");
        Assert.Equal(HttpStatusCode.OK, refreshedDesktopView.Status);

        var eventResponse = await PressAsync(harness, refreshedDesktopView.Body, "dock-files");
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
        var closeResponse = await PressAsync(harness, postFilesDesktopView.Body, $"close-{filesWindow.Id}");
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
        var harness = fixture.Host;

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

    // What the client does on a press: it sends the pressed node's command, from the view it read, to POST /act for the desktop screen.
    private static Task<(HttpStatusCode Status, string Body)> PressAsync(EdgeKernelHarness harness, string viewBody, string key)
    {
        using var view = JsonDocument.Parse(viewBody);
        var command = CommandOf(view.RootElement, key) ?? throw new Xunit.Sdk.XunitException($"The desktop view has no node '{key}' with a command.");
        return SendAsync(harness, HttpMethod.Post, "/act", new
        {
            neuron = IntoChat.OS.Host.Primitives.AppRef.Of(new BrainId(harness.Scope), "desktop").Key,
            target = command.GetProperty("target").GetString(),
            signal = command.GetProperty("signal").GetString(),
            args = command.TryGetProperty("args", out var args) ? args.Clone() : (JsonElement?)null,
        });
    }

    private static JsonElement? CommandOf(JsonElement element, string key)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            if (element.TryGetProperty("key", out var k) && k.ValueKind == JsonValueKind.String && k.GetString() == key
                && element.TryGetProperty("command", out var command) && command.ValueKind == JsonValueKind.Object)
            {
                return command.Clone();
            }
            foreach (var property in element.EnumerateObject())
            {
                if (CommandOf(property.Value, key) is { } found) { return found; }
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                if (CommandOf(item, key) is { } found) { return found; }
            }
        }
        return null;
    }

    private static async Task<(HttpStatusCode Status, string Body)> SendAsync(
        EdgeKernelHarness harness,
        HttpMethod method,
        string relativeBrainRoute,
        object? body = null)
    {
        var path = $"/brains/{harness.Scope}{relativeBrainRoute}";
        using var request = new HttpRequestMessage(method, path);
        request.Headers.Add(PresenceProof.HeaderName, harness.PresenceToken);
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
