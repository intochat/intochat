using System.Text.Json;
using DigitalBrain;
using DigitalBrain.Testing;
using DigitalBrain.UI;
using DigitalBrain.Units;
using IntoChat.OS.Apps.Desktop;
using IntoChat.OS.Apps.Friends;
using IntoChat.OS.Apps.Tests.Unit.Desktop;
using IntoChat.OS.Modules.Apps;

namespace IntoChat.OS.Apps.Tests.Unit.Controls;

// The selectors that were stateful Kit data nodes (Friends' section and scope, Desktop's window tabs) are control.tabStrip children
// with a controlled selection: a select sent through the screen reaches the child, the screen stores the id and draws it back as the
// child's selectedId prop. An id the props do not list is refused by the parent before the child runs.
public sealed class SelectionChildrenFacts
{
    private static JsonElement Select(string id) => JsonSerializer.SerializeToElement(new { id });

    // The props of the child whose neuron id ends with "/{key}", anywhere in the view.
    private static JsonElement? ChildProps(JsonElement node, string key)
    {
        if (node.ValueKind == JsonValueKind.Object)
        {
            if (node.TryGetProperty("kind", out var kind) && kind.GetString() == "neuronRef"
                && node.TryGetProperty("refKind", out var refKind) && refKind.GetString() == "child"
                && node.GetProperty("neuronUri").GetString()!.EndsWith("/" + key, StringComparison.Ordinal))
            {
                return node.GetProperty("props");
            }
            foreach (var property in node.EnumerateObject())
            {
                if (ChildProps(property.Value, key) is { } found) { return found; }
            }
        }
        else if (node.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in node.EnumerateArray())
            {
                if (ChildProps(item, key) is { } found) { return found; }
            }
        }
        return null;
    }

    private static async Task<JsonElement> PropsOf(IScreen screen, string key)
    {
        var view = await screen.Read() ?? throw new InvalidOperationException("The screen has no view.");
        return ChildProps(view.View, key) ?? throw new InvalidOperationException($"The view has no child '{key}'.");
    }

    [Fact]
    public async Task Friends_section_and_scope_round_trip_through_their_tab_strip_children()
    {
        using var person = Edge.AsPerson();
        await using var os = TestOS.Local().RegisterCatalog();
        var friends = await os.Install<FriendsApp>();
        Assert.Equal("all", (await PropsOf(friends.Screen, FriendsApp.SectionKey)).GetProperty("selectedId").GetString());

        await friends.Screen.Dispatch(new Command(FriendsApp.SectionKey, "control.select", Select("feed")));
        await os.Idle();
        await friends.Screen.Dispatch(new Command(FriendsApp.ScopeKey, "control.select", Select("public")));
        await os.Idle();

        Assert.Equal("feed", friends.App.State.ActiveTab);
        Assert.Equal("public", friends.App.State.SelectedScope);
        Assert.Equal("feed", (await PropsOf(friends.Screen, FriendsApp.SectionKey)).GetProperty("selectedId").GetString());
        Assert.Equal("public", (await PropsOf(friends.Screen, FriendsApp.ScopeKey)).GetProperty("selectedId").GetString());
    }

    [Fact]
    public async Task Friends_refuses_a_section_its_props_do_not_list()
    {
        using var person = Edge.AsPerson();
        await using var os = TestOS.Local().RegisterCatalog();
        var friends = await os.Install<FriendsApp>();
        _ = await friends.Screen.Read();

        var refused = await Assert.ThrowsAsync<ActivationRefusedException>(
            () => friends.Screen.Dispatch(new Command(FriendsApp.SectionKey, "control.select", Select("unknown-tab"))));

        Assert.Equal(SelectionRefusals.UnknownItem, refused.Reason);
        Assert.Equal("all", friends.App.State.ActiveTab);
    }

    [Fact]
    public async Task A_desktop_window_tab_round_trips_through_the_windows_tab_strip_child()
    {
        using var person = Edge.AsPerson();
        await using var os = TestOS.Local();
        os.Register<DesktopRecordsNeuron>();
        os.Register<TestViewportNeuron>();
        os.Register<TestWorkspaceNeuron>();
        os.RegisterCatalog();
        os.Register(typeof(DesktopApp).Assembly).Register(typeof(IntoChat.OS.Modules.UI.UiModule).Assembly);
        var desktop = await os.Install<DesktopApp>();
        await desktop.Request("start", new DesktopStart());
        await desktop.Request("open", new DesktopOpen("calculator"));
        var window = desktop.App.State.Windows.First(w => w.Tabs.Any(t => t.Id == "calculator"));
        await desktop.Request("newtab", new DesktopNewTab(window.Id));
        window = desktop.App.State.Windows.First(w => w.Id == window.Id);
        Assert.True(window.Tabs.Length >= 2);
        Assert.NotEqual(window.Tabs[0].Id, window.ActiveTabId);
        var key = DesktopApp.WindowTabsKey(window.Id);

        await desktop.Screen.Dispatch(new Command(key, "control.select", Select($"{window.Id}/0")));
        await os.Idle();

        window = desktop.App.State.Windows.First(w => w.Id == window.Id);
        Assert.Equal(window.Tabs[0].Id, window.ActiveTabId);
        Assert.Equal($"{window.Id}/0", (await PropsOf(desktop.Screen, key)).GetProperty("selectedId").GetString());
    }
}
