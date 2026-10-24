using System.Net.Http.Json;
using System.Text.Json;
using IntoChat.OS.Apps;
using IntoChat.OS.Flutter;
using IntoChat.OS.Flutter.Entries;
using IntoChat.OS.Flutter.Preferences;
using IntoChat.OS.Flutter.Views;
using IntoChat.OS.Flutter.Workspace;
using IntoChat.OS.Circles;

namespace IntoChat.OS.Kernel.Tests.E2E;

// Settings 2.0.0 (a neuron app) on the real script runtime: it opens as a desktop window on the given tab, every tab
// renders from the value tree, preference controls and the owner-only entries route agree with the stores, and a saved
// key never reaches the tree, a neuron's state or the activity feed.
[Collection("Reference host")]
public sealed class SettingsAppFacts(ReferenceBrainFixture host) : BrainFact(host)
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(90);
    private const string Canary = "fixture-value-0001";

    private string Desktop => WorkspaceId + "/apps/desktop";
    private string Settings => WorkspaceId + "/apps/settings";

    [Fact(Timeout = 360_000)]
    public async Task TheSettingsAppIsVersion200()
    {
        var ct = TestContext.Current.CancellationToken;
        using var caller = User.AsCaller();
        Assert.Equal("2.0.0", ShippedAppsFixture.Features.Single(feature => feature.Owner == "os" && feature.Bundle.Name == "settings").Bundle.Version);
        await StartDesktop(ct);
        var installed = await TestWait.UntilAsync(_ => Brain.Get<IApp>(Settings).Read(), snapshot => snapshot.Status == AppStatus.Live, Patience, ct);
        Assert.Equal("2.0.0", installed.Version);
    }

    [Fact(Timeout = 360_000)]
    public async Task OpeningSettingsFromTheDesktopLandsOnTheGivenTab()
    {
        var ct = TestContext.Current.CancellationToken;
        using var caller = User.AsCaller();
        await StartDesktop(ct);
        // More, then Settings: its window opens on General.
        var more = await TestWait.UntilAsync(_ => Brain.Get<IntoChat.OS.Flutter.Button.IButton>(Desktop + "/settings").Read(),
            button => button.Activation?.Contains("\"workspace\"", StringComparison.Ordinal) == true, Patience, ct);
        await Activate(more.Activation!, ct);
        var opened = await TestWait.UntilAsync(_ => Brain.Get<IWorkspace>(WorkspaceId).Read(), state => state.FocusedViewId == "settings", Patience, ct);
        Assert.Contains(opened.Windows, window => window.Tabs.Any(tab => tab.Id == "settings"));
        var general = await ViewUntil(nodes => nodes.Any(node => node.Key == "tab-general" && node.Style == UiStyle.Primary), ct);
        Assert.Equal(UiStyle.Ghost, Assert.Single(general, node => node.Key == "tab-keys").Style);

        // Ino's "Add an API key" is the shell's settings activation on keys, which it hands the root as a workspace
        // activation opening Settings on that tab.
        await Activate(JsonSerializer.Serialize(new { kind = "workspace", brainId = WorkspaceId, application = "settings", operation = "open", input = "keys" }), ct);
        var keys = await ViewUntil(nodes => nodes.Any(node => node.Key == "tab-keys" && node.Style == UiStyle.Primary), ct);
        Assert.Equal(UiStyle.Ghost, Assert.Single(keys, node => node.Key == "tab-general").Style);
        Assert.Contains(keys, node => node.Key == "key-field-openai");
    }

    [Fact(Timeout = 360_000)]
    public async Task EveryTabRendersFromTheValueTree()
    {
        var ct = TestContext.Current.CancellationToken;
        using var caller = User.AsCaller();
        await StartDesktop(ct);
        await Activate(JsonSerializer.Serialize(new { kind = "workspace", brainId = WorkspaceId, application = "settings", operation = "open", input = "general" }), ct);
        var expected = new Dictionary<string, string[]>
        {
            ["general"] = ["pref-display-name", "pref-theme", "pref-compact", "pref-reduced-motion", "pref-new-tab"],
            ["models"] = ["models", "pref-default-model"],
            ["keys"] = ["keys", "key-field-openai", "key-field-anthropic"],
            ["connections"] = ["connections"],
            ["variables"] = ["variables-field"],
            ["notifications"] = ["pref-notify-agent-finished", "pref-notify-needs-approval", "pref-notify-connection-errors", "pref-notify-product-updates"],
            ["archived"] = ["archived-empty"],
            ["danger"] = ["delete-account", "delete"],
        };
        foreach (var (id, _) in SettingsTabsOrder())
        {
            await ClickView("tab-" + id, "tab:" + id, ct);
            var nodes = await ViewUntil(shown => shown.Any(node => node.Key == "tab-body-" + id) && shown.Any(node => node.Key == "tab-" + id && node.Style == UiStyle.Primary), ct);
            foreach (var key in expected[id]) { Assert.Contains(nodes, node => node.Key == key); }
        }
        // The Billing tab is shown disabled, never selected.
        Assert.False(Assert.Single(await ViewUntil(_ => true, ct), node => node.Key == "tab-billing").Enabled);
    }

    [Fact(Timeout = 360_000)]
    public async Task NotificationsPreferenceAgreesWithTheRouteAndNewTabAppOpensIt()
    {
        var ct = TestContext.Current.CancellationToken;
        using var caller = User.AsCaller();
        await StartDesktop(ct);
        await Activate(JsonSerializer.Serialize(new { kind = "workspace", brainId = WorkspaceId, application = "settings", operation = "open", input = "notifications" }), ct);
        var nodes = await ViewUntil(shown => shown.Any(node => node.Key == "pref-notify-product-updates"), ct);
        var reference = Assert.Single(nodes, node => node.Key == "pref-notify-product-updates").Reference!;
        var control = Brain.Get<IPreferenceControl>(reference.Name);
        var current = (await TestWait.UntilAsync(_ => control.Read(), state => state.Spec is not null && state.Value is not null, Patience, ct)).Value;
        var flipped = current == "on" ? "off" : "on";
        await control.Set(flipped);
        using var read = await User.Http.GetAsync($"/brains/{WorkspaceId}/preferences/notifications", ct);
        read.EnsureSuccessStatusCode();
        Assert.Equal(flipped == "on", (await read.Content.ReadFromJsonAsync<JsonElement>(ct)).GetProperty("product-updates").GetBoolean());
        Assert.Equal(flipped, (await control.Read()).Value);

        // The New tab app preference: the desktop's + opens the chosen app (Ino by default) as a new tab.
        await Activate(JsonSerializer.Serialize(new { kind = "workspace", brainId = WorkspaceId, application = "calculator" }), ct);
        await TestWait.UntilAsync(_ => Brain.Get<IWorkspace>(WorkspaceId).Read(), state => state.Windows.Any(window => window.Tabs.Any(tab => tab.Id == "calculator")), Patience, ct);
        using var chosen = await User.Http.PutAsJsonAsync($"/brains/{WorkspaceId}/preferences/workspace", new { newTabApp = "calculator" }, ct);
        chosen.EnsureSuccessStatusCode();
        var before = (await Brain.Get<IWorkspace>(WorkspaceId).Read()).Windows.Sum(window => window.Tabs.Length);
        await ClickDesktop("newtab", ct);
        var after = await TestWait.UntilAsync(_ => Brain.Get<IWorkspace>(WorkspaceId).Read(),
            state => state.Windows.Sum(window => window.Tabs.Length) > before, Patience, ct);
        Assert.True(after.Windows.Sum(window => window.Tabs.Count(tab => tab.Id == "calculator")) >= 1);
    }

    [Fact(Timeout = 360_000)]
    public async Task OwnerOnlyEntryWrites()
    {
        var ct = TestContext.Current.CancellationToken;
        using var member = await Brain.PersonAsync("member-" + Guid.NewGuid().ToString("N"), ct);
        // A circle's brain: the creator is its owner, an invited person who accepts is a member.
        string circle;
        using (User.AsCaller()) { circle = await Brain.Get<ICircleFactory>(ICircleFactory.Key(User.BrainId)).Create("keys-room").WaitAsync(ct); }
        using (User.AsCallerIn(circle)) { await Brain.Get<ICircle>(ICircle.Key(circle)).Invite(member.AccountId).WaitAsync(ct); }
        using (member.AsCallerIn(circle)) { await Brain.Get<ICircle>(ICircle.Key(circle)).Accept().WaitAsync(ct); }
        using (var selected = await member.Http.PostAsJsonAsync("/identity/session/brain", new { member.AccountId, BrainId = circle }, ct)) { selected.EnsureSuccessStatusCode(); }

        using var refused = await member.Http.PutAsJsonAsync($"/brains/{circle}/entries/provider:openai", new { kind = "secret", value = Canary }, ct);
        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        using var owner = await User.Http.PutAsJsonAsync($"/brains/{circle}/entries/provider:openai", new { kind = "secret", value = Canary }, ct);
        Assert.Equal(HttpStatusCode.NoContent, owner.StatusCode);
    }

    [Fact(Timeout = 360_000)]
    public async Task AKeySavedInSettingsNeverReachesTheTreeOrLog()
    {
        var ct = TestContext.Current.CancellationToken;
        using var caller = User.AsCaller();
        await StartDesktop(ct);
        await Activate(JsonSerializer.Serialize(new { kind = "workspace", brainId = WorkspaceId, application = "settings", operation = "open", input = "keys" }), ct);
        await ViewUntil(nodes => nodes.Any(node => node.Key == "key-field-openai"), ct);
        using (var put = await User.Http.PutAsJsonAsync($"/brains/{WorkspaceId}/entries/provider:openai", new { kind = "secret", value = Canary }, ct))
        { Assert.Equal(HttpStatusCode.NoContent, put.StatusCode); }
        // Settings refreshes on the change; give it a render before scanning.
        await ClickView("tab-general", "tab:general", ct);
        await ClickView("tab-keys", "tab:keys", ct);
        await ViewUntil(nodes => nodes.Any(node => node.Key == "key-field-openai"), ct);

        var scanned = new List<string>();
        var view = await Brain.Get<IView>(Settings + "/view").Read();
        scanned.Add(JsonSerializer.Serialize(view));
        var app = await Brain.Get<IApp>(Settings).Read();
        scanned.Add(JsonSerializer.Serialize(app.Activity));
        scanned.Add(JsonSerializer.Serialize(app.Presentation));
        await foreach (var instance in Brain.Kernel.Instances<IEntryControl>(ct))
        { if (instance.Key.StartsWith(Settings + "/", StringComparison.Ordinal)) { scanned.Add(JsonSerializer.Serialize(await Brain.Get<IEntryControl>(instance.Key).Read())); } }
        await foreach (var instance in Brain.Kernel.Instances<IPreferenceControl>(ct))
        { if (instance.Key.StartsWith(Settings + "/", StringComparison.Ordinal)) { scanned.Add(JsonSerializer.Serialize(await Brain.Get<IPreferenceControl>(instance.Key).Read())); } }
        scanned.Add(await LiveReplay("AppActivity", ct));
        Assert.True(scanned.Count > 4);
        Assert.All(scanned, text => Assert.DoesNotContain(Canary, text, StringComparison.Ordinal));
    }

    // The retained AppActivity replay up to the stream's ready marker, as the shell's watchLive reads it.
    private async Task<string> LiveReplay(string type, CancellationToken ct)
    {
        using var response = await User.Http.GetAsync($"/brains/{WorkspaceId}/live?type={type}", HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();
        using var reader = new StreamReader(await response.Content.ReadAsStreamAsync(ct));
        var text = new System.Text.StringBuilder();
        while (await reader.ReadLineAsync(ct).AsTask().WaitAsync(TimeSpan.FromSeconds(10), ct) is { } line)
        {
            text.AppendLine(line);
            if (line.StartsWith("data: ", StringComparison.Ordinal) && JsonDocument.Parse(line["data: ".Length..]).RootElement.GetProperty("type").GetString() == "ready") { break; }
        }
        return text.ToString();
    }

    private static IEnumerable<(string Id, string Label)> SettingsTabsOrder()
        => [("general", ""), ("models", ""), ("keys", ""), ("connections", ""), ("variables", ""), ("notifications", ""), ("archived", ""), ("danger", "")];

    private async Task StartDesktop(CancellationToken ct)
    {
        await Brain.Get<IntoChat.OS.Flutter.Text.IText>(Desktop + "/layout").Set("desktop");
        await Brain.Get<IApp>(Desktop).Configure(new(), []);
        await AppRuntimeReadiness.WaitAsync(Brain, User, ct);
        using var connected = await User.Http.PostAsJsonAsync($"/brains/{WorkspaceId}/apps/connect", new { }, ct);
        connected.EnsureSuccessStatusCode();
    }

    // The receiver subscribes from its start: an activation sent while a connect's update restarts it is not seen, so
    // (as a person clicks again) it is sent again until the window shows.
    private async Task Activate(string activation, CancellationToken ct)
    {
        var target = JsonDocument.Parse(activation).RootElement.GetProperty("application").GetString()!;
        var workspace = Brain.Get<IWorkspace>(WorkspaceId);
        for (var attempt = 0; attempt < 12; attempt++)
        {
            using var response = await User.Http.PostAsJsonAsync($"/brains/{WorkspaceId}/apps/workspace/request", new { operation = "activate", input = activation }, ct);
            response.EnsureSuccessStatusCode();
            try { await TestWait.UntilAsync(_ => workspace.Read(), state => state.FocusedViewId == target, TimeSpan.FromSeconds(10), ct); return; }
            catch (TimeoutException) { }
        }
        throw new TimeoutException("No window focused " + target + ".");
    }

    private async Task ClickDesktop(string action, CancellationToken ct)
    {
        var view = Brain.Get<IView>(Desktop + "/view");
        var published = await TestWait.UntilAsync(_ => view.Read(),
            current => current is not null && Flatten(current.Root).Any(node => node.Kind == UiNodeKind.Button && node.Action == action), Patience, ct);
        var button = Flatten(published!.Root).First(node => node.Kind == UiNodeKind.Button && node.Action == action);
        await view.Activate(published.Generation, button.Key, action, Guid.NewGuid());
    }

    private async Task ClickView(string key, string action, CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            var view = Brain.Get<IView>(Settings + "/view");
            var published = await TestWait.UntilAsync(_ => view.Read(),
                current => current is not null && Flatten(current.Root).Any(node => node.Key == key && node.Action == action && node.Enabled), Patience, ct);
            try { await view.Activate(published!.Generation, key, action, Guid.NewGuid()); return; }
            catch (StaleActionException) when (attempt < 5) { }
        }
    }

    private async Task<UiNode[]> ViewUntil(Func<UiNode[], bool> ready, CancellationToken ct)
    {
        var view = Brain.Get<IView>(Settings + "/view");
        try
        {
            var published = await TestWait.UntilAsync(_ => view.Read(), current => current is not null && ready([.. Flatten(current.Root)]), Patience, ct);
            return [.. Flatten(published!.Root)];
        }
        catch (TimeoutException)
        {
            var last = await view.Read();
            throw new TimeoutException("Settings showed: " + string.Join(", ", last is null ? [] : Flatten(last.Root).Select(node => node.Key + "=" + node.Action)));
        }
    }

    private static IEnumerable<UiNode> Flatten(UiNode root)
    {
        yield return root;
        foreach (var child in root.Children.SelectMany(Flatten)) { yield return child; }
    }
}
