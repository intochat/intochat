using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Microsoft.Playwright;

namespace DigitalBrain.OS.Tests.E2E;

public sealed class ShellPersistenceFacts(ReferenceBrainFixture host) : BrainFact(host)
{
    [Fact(Timeout = 300_000)]
    public async Task FreshBrowserRestoresWorkspaceWithoutDeviceStorage()
    {
        var ct = TestContext.Current.CancellationToken;
        var brain = Brain;
        var page = await OpenPageAsync(ct);
        // The shell acknowledges durability with the save that carries the project; the wait is on that response.
        var saved = page.WaitForResponseAsync(response => response.Ok && response.Request.Method == "PUT"
            && response.Url.EndsWith("/shell/state", StringComparison.Ordinal)
            && response.Request.PostData?.Contains("Cloud-only workspace", StringComparison.Ordinal) == true);
        var id = await WorkspaceBrowser.CreateProjectAsync(page, "Cloud-only workspace");
        await saved.WaitAsync(ct);
        var state = await brain.HttpClient.GetFromJsonAsync<JsonObject>("/shell/state", ct);
        Assert.Contains(state?["snapshot"]?["projects"]?.AsArray() ?? [], project => project?["id"]?.GetValue<string>() == id);
        await using var fresh = await brain.OpenBrowserAsync(ReferenceBrainFixture.Browser, ct);
        await fresh.Page.GetByRole(AriaRole.Button, new() { NameRegex = new System.Text.RegularExpressions.Regex("^Workspaces") }).ClickAsync();
        await Assertions.Expect(fresh.Page.GetByRole(AriaRole.Menuitemcheckbox, new() { Name = "Cloud-only workspace", Exact = true })).ToBeVisibleAsync();
        Assert.Equal(0, await fresh.Page.EvaluateAsync<int>("() => Object.keys(localStorage).filter(k => k.includes('workspace')).length"));
    }
}
