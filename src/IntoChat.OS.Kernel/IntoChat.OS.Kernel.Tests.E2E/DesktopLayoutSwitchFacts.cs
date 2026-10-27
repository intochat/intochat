using System.Net.Http.Json;
using System.Text.Json;
using IntoChat.OS.Modules.Flutter.Button;
using IntoChat.OS.Modules.Flutter.Text;

namespace IntoChat.OS.Kernel.Tests.E2E;

// On the real script runtime: More's Classic layout and v1's New desktop carry the shell's reconnect
// activation; the `layout` request they make switches the next connect's root, and the open views stay.
[Collection("Reference host")]
public sealed class DesktopLayoutSwitchFacts(ReferenceBrainFixture host) : BrainFact(host)
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(90);

    [Fact(Timeout = 360_000)]
    public async Task SwitchingClassicAndBackChangesTheRootAndKeepsOpenViews()
    {
        var ct = TestContext.Current.CancellationToken;
        using var caller = User.AsCaller();
        var desktop = WorkspaceId + "/apps/desktop";
        var layout = Brain.Get<IText>(desktop + "/layout");
        await layout.Set("desktop");
        await AppRuntimeReadiness.WaitAsync(Brain, User, ct);
        Assert.Equal("view", (await Connect(ct)).GetProperty("kind").GetString());
        var classic = await TestWait.UntilAsync(_ => Brain.Get<IButton>(desktop + "/classic").Read(), button => button.Activation is not null, Patience, ct);
        var open = (await Brain.Get<IntoChat.OS.Modules.Flutter.Workspace.IWorkspace>(IntoChat.OS.Modules.Flutter.Workspace.IWorkspace.Key(WorkspaceId)).Read()).OpenViews.Select(view => view.Id).ToArray();

        for (var round = 0; round < 2; round++)
        {
            await Request(classic.Activation!, ct);
            await TestWait.UntilAsync(_ => layout.Read(), text => text.Markdown == "classic", Patience, ct);
            var root = await Connect(ct);
            Assert.Equal(("layout", WorkspaceId + "/apps/workspace/root"), (root.GetProperty("kind").GetString(), root.GetProperty("name").GetString()));
            var back = await TestWait.UntilAsync(_ => Brain.Get<IButton>(WorkspaceId + "/apps/workspace/new-desktop").Read(), button => button.Activation is not null, Patience, ct);
            await Request(back.Activation!, ct);
            await TestWait.UntilAsync(_ => layout.Read(), text => text.Markdown == "desktop", Patience, ct);
            var desktopRoot = await Connect(ct);
            Assert.Equal(("view", desktop + "/view"), (desktopRoot.GetProperty("kind").GetString(), desktopRoot.GetProperty("name").GetString()));
            Assert.Equal(open, (await Brain.Get<IntoChat.OS.Modules.Flutter.Workspace.IWorkspace>(IntoChat.OS.Modules.Flutter.Workspace.IWorkspace.Key(WorkspaceId)).Read()).OpenViews.Select(view => view.Id));
        }
    }

    // What the shell does with a reconnect activation: hand the app its request (then connect again).
    private async Task Request(string activation, CancellationToken ct)
    {
        var action = JsonDocument.Parse(activation).RootElement;
        Assert.Equal(("reconnect", WorkspaceId), (action.GetProperty("kind").GetString(), action.GetProperty("brainId").GetString()));
        using var response = await User.Http.PostAsJsonAsync($"/brains/{WorkspaceId}/apps/{action.GetProperty("application").GetString()}/request",
            new { operation = action.GetProperty("operation").GetString(), input = action.GetProperty("input").GetString() }, ct);
        response.EnsureSuccessStatusCode();
    }

    private async Task<JsonElement> Connect(CancellationToken ct)
    {
        using var connected = await User.Http.PostAsJsonAsync($"/brains/{WorkspaceId}/apps/connect", new { }, ct);
        connected.EnsureSuccessStatusCode();
        return (await connected.Content.ReadFromJsonAsync<JsonElement>(ct)).GetProperty("root").Clone();
    }
}
