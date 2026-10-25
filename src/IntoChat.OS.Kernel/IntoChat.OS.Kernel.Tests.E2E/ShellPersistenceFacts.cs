using IntoChat.OS.Modules.Flutter.Workspace;
using Microsoft.Playwright;

namespace IntoChat.OS.Kernel.Tests.E2E;

[Collection("Reference host")]
public sealed class ShellPersistenceFacts(ReferenceBrainFixture host) : BrainFact(host)
{
    [Fact(Timeout = 120_000)]
    public async Task FreshBrowserRestoresWorkspaceWithoutDeviceStorage()
    {
        var ct = TestContext.Current.CancellationToken;
        using var caller = User.AsCaller();
        var page = await OpenPageAsync(ct);
        var workspace = Brain.Get<IWorkspace>(WorkspaceId);
        // The dock's Ino, the window's tab and header are all buttons named Ino (the first run may already have opened its window).
        await page.GetByRole(AriaRole.Button, new() { Name = "Ino", Exact = true }).First.ClickAsync();
        await TestWait.UntilAsync(_ => workspace.Read(), value => value.FocusedViewId == "ino", TimeSpan.FromSeconds(15), ct);
        await Assertions.Expect(page.GetByRole(AriaRole.Button, new() { Name = "New chat", Exact = true })).ToBeVisibleAsync();

        // A fresh context has no cookies or device preferences. Signing into the same
        // account must restore the durable open view from the server.
        var fresh = await OpenPageAsync(ct);
        await Assertions.Expect(fresh.GetByRole(AriaRole.Button, new() { Name = "New chat", Exact = true })).ToBeVisibleAsync();
        Assert.Equal("ino", (await workspace.Read()).FocusedViewId);
        Assert.Equal(0, await fresh.EvaluateAsync<int>("() => Object.keys(localStorage).filter(k => k.includes('workspace')).length"));
    }
}
