using IntoChat.OS.Flutter;
using IntoChat.OS.Flutter.Views;
using Xunit;

namespace IntoChat.OS.Kernel.Tests.Integration;

// The Settings value tree as a real IView accepts it: every tab, and long lists, publish within IView's limits.
public sealed class OsSettingsViewHostFacts
{
    private const string App = "b1/apps/settings";
    private static readonly DateTimeOffset Now = new(2026, 10, 24, 12, 0, 0, TimeSpan.Zero);

    private static SettingsModel Model(string tab = "general", bool confirm = false, string? toast = null, int archived = 2, int total = 2) => new(
        tab, App, new("auto", "Automatic", true),
        [new("profile:fast", "Fast", true), new("preset:Big", "Big", false)],
        [.. Enumerable.Range(0, archived).Select(i => new ArchivedRow("Trip " + i, Now.AddHours(-i - 1)))], total,
        [new("gmail", "Gmail", "Connected", true), new("salesforce", "Salesforce", "Not connected", false)],
        confirm, toast);

    private static IEnumerable<UiNode> All(UiNode node) => node.Children.SelectMany(All).Prepend(node);

    private static UiNode Find(UiNode root, string key) => All(root).Single(node => node.Key == key);

    // Every tab, with the modal and a toast, is accepted by IView as a ViewPublisher publishes it.
    [Fact]
    public async Task TheTreePassesViewValidation()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await TestBrain.Create().WithModule<IntoChat.OS.Apps.AppsModule>().WithModule<FlutterModule>().StartAsync(ct);
        var person = await brain.PersonAsync("settings", ct);
        using var caller = person.AsCaller();
        var app = IntoChat.OS.Apps.IApp.Key(person.BrainId, "settings");
        var view = brain.Get<IView>(app + "/view");
        var publisher = new ViewPublisher(view, 1);
        foreach (var (tab, _) in SettingsTabs.All)
        {
            await publisher.Publish(SettingsView.Render(Model(tab, confirm: true, toast: "Saved.", total: 150) with { AppKey = app }, Now));
            Assert.Equal("tab-body-" + tab, (await view.Read())!.Root.Children[1].Key);
        }
    }

    // A full archived page and many connections (two of one connector) stay unique-keyed and inside IView's limits.
    [Fact]
    public async Task LongListsAreCappedAndKeyedByRow()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await TestBrain.Create().WithModule<IntoChat.OS.Apps.AppsModule>().WithModule<FlutterModule>().StartAsync(ct);
        var person = await brain.PersonAsync("settings-long", ct);
        using var caller = person.AsCaller();
        var app = IntoChat.OS.Apps.IApp.Key(person.BrainId, "settings");
        var publisher = new ViewPublisher(brain.Get<IView>(app + "/view"), 1);

        var archived = SettingsView.Render(Model("archived", archived: 150, total: 150) with { AppKey = app }, Now);
        Assert.Equal(SettingsView.ArchivedPage, Find(archived, "archived").Children.Length);
        Assert.True(All(archived).Count() < 512);
        await publisher.Publish(archived);

        ConnectionRow[] rows = [new("gmail", "Gmail work", "Not connected", false), new("gmail", "Gmail home", "Not connected", false),
            .. Enumerable.Range(0, 100).Select(i => new ConnectionRow("salesforce", "Org " + i, "Not connected", false))];
        var connections = SettingsView.Render(Model("connections") with { AppKey = app, Connections = rows }, Now);
        var list = Find(connections, "connections");
        Assert.Equal(SettingsView.ConnectionsShown, list.Children.Length);
        Assert.Equal(["connection-0-connect", "connection-1-connect"], list.Children.Take(2).Select(row => row.Children.Single().Key));
        Assert.All(list.Children.Take(2), row => Assert.Equal(app + "/connect/gmail", row.Children.Single().Reference!.Name));
        await publisher.Publish(connections);
        Assert.Equal("tab-body-connections", (await brain.Get<IView>(app + "/view").Read())!.Root.Children[1].Key);
    }
}
