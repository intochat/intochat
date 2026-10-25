using System.Net.Http.Json;
using System.Text.Json;
using IntoChat.OS.Modules.Apps;
using IntoChat.OS.Modules.Flutter.Button;
using IntoChat.OS.Modules.Flutter.Views;
using IntoChat.OS.Modules.Flutter.Workspace;

namespace IntoChat.OS.Kernel.Tests.E2E;

// Apps 1.0.0 (a neuron app) on the real script runtime: the dock opens its library; a detail asks for what an app
// needs and Install is the person's own request with the consent it showed; updates that ask for more ask again;
// Remove asks first. The shell's POST of an install/update/remove activation is the person's own HTTP call here.
[Collection("Reference host")]
public sealed class AppsAppFacts(ReferenceBrainFixture host) : BrainFact(host)
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(90);
    private const string Slot = "mail";

    private string Desktop => WorkspaceId + "/apps/desktop";
    private string Apps => WorkspaceId + "/apps/apps";

    [Fact(Timeout = 360_000)]
    public async Task TheAppsAppIsVersion100AndSettings200()
    {
        Assert.Equal("1.0.0", ShippedAppsFixture.Features.Single(feature => feature.Owner == "os" && feature.Bundle.Name == "apps").Bundle.Version);
        Assert.Equal("2.0.1", ShippedAppsFixture.Features.Single(feature => feature.Owner == "os" && feature.Bundle.Name == "settings").Bundle.Version);
        var ct = TestContext.Current.CancellationToken;
        using var caller = User.AsCaller();
        await StartDesktop(ct);
        var installed = await TestWait.UntilAsync(_ => Brain.Get<IApp>(Apps).Read(), snapshot => snapshot.Status == AppStatus.Live, Patience, ct);
        Assert.Equal("1.0.0", installed.Version);
        Assert.Equal("2.0.1", (await Brain.Get<IApp>(WorkspaceId + "/apps/settings").Read()).Version);
    }

    [Fact(Timeout = 360_000)]
    public async Task DockAppsOpensTheLibrary()
    {
        var ct = TestContext.Current.CancellationToken;
        using var caller = User.AsCaller();
        await StartDesktop(ct);
        await ClickDesktop("open:apps", ct);
        var opened = await TestWait.UntilAsync(_ => Brain.Get<IWorkspace>(WorkspaceId).Read(), state => state.FocusedViewId == "apps", Patience, ct);
        Assert.Contains(opened.Windows, window => window.Tabs.Any(tab => tab.Id == "apps"));
        var library = await ViewUntil(nodes => nodes.Any(node => node.Key == "installed") && nodes.Any(node => node.Key == "available")
            && nodes.Any(node => node.Kind == UiNodeKind.Button && node.Action?.StartsWith("detail:", StringComparison.Ordinal) == true), ct);
        Assert.Contains(library, node => node.Key == "apps-title");
        Assert.Contains(library, node => node.Key == "connections");
    }

    // The reference host's only connection kinds are OAuth ones (Gmail, Salesforce) that no stub here can complete, so a
    // bound connection is not reachable end to end: UnifiedInstallFacts covers applying bindings. What is observable
    // here is that a slot with no connection keeps Install disarmed and Add new armed for the slot's own connection.
    [Fact(Timeout = 360_000)]
    public async Task AnUnboundSlotKeepsInstallDisarmed()
    {
        var ct = TestContext.Current.CancellationToken;
        var name = "mailer" + Guid.NewGuid().ToString("N")[..8];
        var requires = new AppRequirements(["person.birthDate"], ["gmail"]);
        var publisher = await Publish(name, requires, ct);
        using var caller = User.AsCaller();
        await StartDesktop(ct);

        await OpenApps($"app:{publisher}/{name}", ct);
        var detail = await ViewUntil(nodes => nodes.Any(node => node.Key == "requires") && nodes.Any(node => node.Key == "install"), ct);
        Assert.Contains(detail, node => node.Key == "data-0" && node.Text == "person.birthDate");
        Assert.Contains(detail, node => node.Key == "slot-name-0" && node.Text == Slot);
        Assert.Contains(detail, node => node.Key == "install-hint");
        var install = await TestWait.UntilAsync(_ => Brain.Get<IButton>(Apps + "/install").Read(), state => !state.Enabled && state.Activation is null, Patience, ct);
        Assert.Equal("Install", install.Label);
        var add = await ArmedButton(Apps + "/slot-new/0", ct);
        var connect = JsonDocument.Parse(add.Activation!).RootElement;
        Assert.Equal(("connections", WorkspaceId, "gmail", name + "-" + Slot),
            (connect.GetProperty("kind").GetString(), connect.GetProperty("brainId").GetString(), connect.GetProperty("connector").GetString(), connect.GetProperty("connection").GetString()));
        Assert.Equal("", (await Brain.Get<IApp>(IApp.Key(WorkspaceId, name)).Read()).Manifest.Title);
    }

    [Fact(Timeout = 360_000)]
    public async Task InstallAsksForRequirementsAndGrantsWhatWasConsentedTo()
    {
        var ct = TestContext.Current.CancellationToken;
        var name = "reader" + Guid.NewGuid().ToString("N")[..8];
        var requires = new AppRequirements(["person.birthDate"]);
        var publisher = await Publish(name, requires, ct);
        using var caller = User.AsCaller();
        await StartDesktop(ct);

        await OpenApps($"app:{publisher}/{name}", ct);
        var detail = await ViewUntil(nodes => nodes.Any(node => node.Key == "requires") && nodes.Any(node => node.Key == "install"), ct);
        Assert.Contains(detail, node => node.Key == "data-0" && node.Text == "person.birthDate");
        // Install is the person's own request carrying the consent of what the panel showed.
        var install = await ArmedButton(Apps + "/install", ct);
        var activation = JsonDocument.Parse(install.Activation!).RootElement;
        Assert.Equal("install", activation.GetProperty("kind").GetString());
        var request = activation.GetProperty("request");
        Assert.Equal(AppConsent.Of(requires), request.GetProperty("consent").GetString());
        // A stale consent installs nothing.
        using (var stale = await User.Http.PostAsJsonAsync($"/brains/{WorkspaceId}/apps/install",
            new { @ref = new { publisher, app = name }, consent = AppConsent.Of(null) }, ct))
        { Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode); }
        Assert.Equal("", (await Brain.Get<IApp>(IApp.Key(WorkspaceId, name)).Read()).Manifest.Title);

        using (var installed = await User.Http.PostAsJsonAsync($"/brains/{WorkspaceId}/apps/install", request, ct))
        {
            installed.EnsureSuccessStatusCode();
            Assert.Equal(name, (await installed.Content.ReadFromJsonAsync<JsonElement>(ct)).GetProperty("installedName").GetString());
        }
        Assert.Equal(requires, (await Brain.Get<IApp>(IApp.Key(WorkspaceId, name)).Read()).Origin!.Consented);
        // The result opens the app in a window (the shell's workspace activation).
        await Activate(JsonSerializer.Serialize(new { kind = "workspace", brainId = WorkspaceId, application = name, operation = "open" }), name, ct);
    }

    [Fact(Timeout = 360_000)]
    public async Task UpdateWithNewRequirementsAsksAgain()
    {
        var ct = TestContext.Current.CancellationToken;
        var name = "growing" + Guid.NewGuid().ToString("N")[..8];
        using var author = await Brain.PersonAsync("author-" + Guid.NewGuid().ToString("N"), ct);
        var app = await Publish(author, name, null);
        var publisher = await PublisherOf(name, ct);
        using var caller = User.AsCaller();
        await StartDesktop(ct);
        using (var first = await User.Http.PostAsJsonAsync($"/brains/{WorkspaceId}/apps/install",
            new { @ref = new { publisher, app = name }, consent = AppConsent.Of(null) }, ct))
        { first.EnsureSuccessStatusCode(); }

        // The next release reads the birth date: the update waits for consent.
        var more = new AppRequirements(["person.birthDate"]);
        using (author.AsCaller())
        {
            await app.SaveManifest(Manifest(name, more), Guid.NewGuid());
            await app.Publish();
        }
        await OpenApps("installed:" + name, ct);
        var detail = await ViewUntil(nodes => nodes.Any(node => node.Key == "update") && nodes.Any(node => node.Key == "data-0"), ct);
        Assert.Contains(detail, node => node.Key == "data-0" && node.Text == "person.birthDate");
        var update = JsonDocument.Parse((await ArmedButton(Apps + "/update", ct)).Activation!).RootElement;
        Assert.Equal(AppConsent.Of(more), update.GetProperty("consent").GetString());

        using (var stale = await User.Http.PostAsJsonAsync($"/brains/{WorkspaceId}/apps/{name}/update", new { consent = AppConsent.Of(null) }, ct))
        { Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode); }
        Assert.Null((await Brain.Get<IApp>(IApp.Key(WorkspaceId, name)).Read()).Origin!.Consented?.SemanticTypes);
        using (var consented = await User.Http.PostAsJsonAsync($"/brains/{WorkspaceId}/apps/{name}/update", new { consent = update.GetProperty("consent").GetString() }, ct))
        { consented.EnsureSuccessStatusCode(); }
        Assert.Equal(more, (await Brain.Get<IApp>(IApp.Key(WorkspaceId, name)).Read()).Origin!.Consented);
    }

    [Fact(Timeout = 360_000)]
    public async Task ShareLinkOpensTheDetailAfterSignIn()
    {
        var ct = TestContext.Current.CancellationToken;
        var name = "shared" + Guid.NewGuid().ToString("N")[..8];
        var publisher = await Publish(name, null, ct);
        using var caller = User.AsCaller();
        await StartDesktop(ct);
        // After sign-in the shell hands the link's target to Apps as `open`; the detail shows with its share control
        // (this host has no PublicOrigin, so sharing reads as off rather than offering a link).
        await OpenApps($"app:{publisher}/{name}", ct);
        var detail = await ViewUntil(nodes => nodes.Any(node => node.Key == "app-detail") && nodes.Any(node => node.Key is "share" or "share-off"), ct);
        Assert.Contains(detail, node => node.Key == "detail-display" && node.Text!.EndsWith("/" + name, StringComparison.Ordinal));
        Assert.Contains(detail, node => node.Key == "install");
        Assert.DoesNotContain(detail, node => node.Key == "remove-ask");
    }

    [Fact(Timeout = 360_000)]
    public async Task RemoveAsksThenRemoves()
    {
        var ct = TestContext.Current.CancellationToken;
        var name = "doomed" + Guid.NewGuid().ToString("N")[..8];
        var publisher = await Publish(name, null, ct);
        using var caller = User.AsCaller();
        await StartDesktop(ct);
        using (var first = await User.Http.PostAsJsonAsync($"/brains/{WorkspaceId}/apps/install",
            new { @ref = new { publisher, app = name }, consent = AppConsent.Of(null) }, ct))
        { first.EnsureSuccessStatusCode(); }
        await OpenApps("installed:" + name, ct);
        await ViewUntil(nodes => nodes.Any(node => node.Key == "remove-ask"), ct);
        // Armed only while the confirm Modal shows.
        Assert.Null((await Brain.Get<IButton>(Apps + "/remove").Read()).Activation);
        await ClickView("remove-ask", "remove-ask", ct);
        await ViewUntil(nodes => nodes.Any(node => node.Key == "confirm-remove"), ct);
        var remove = JsonDocument.Parse((await ArmedButton(Apps + "/remove", ct)).Activation!).RootElement;
        Assert.Equal(("remove", true, name), (remove.GetProperty("kind").GetString(), remove.GetProperty("confirmed").GetBoolean(), remove.GetProperty("name").GetString()));
        // Cancel disarms it again and the app stays.
        await ClickView("remove-cancel", "remove-cancel", ct);
        await TestWait.UntilAsync(_ => Brain.Get<IButton>(Apps + "/remove").Read(), state => state.Activation is null, Patience, ct);
        Assert.NotEqual("", (await Brain.Get<IApp>(IApp.Key(WorkspaceId, name)).Read()).Manifest.Title);

        // The confirmed removal is the shell's DELETE as the person.
        using var removed = await User.Http.DeleteAsync($"/brains/{WorkspaceId}/apps/{name}", ct);
        removed.EnsureSuccessStatusCode();
        Assert.Equal("", (await Brain.Get<IApp>(IApp.Key(WorkspaceId, name)).Read()).Manifest.Title);
    }

    private AppManifest Manifest(string name, AppRequirements? requires)
        => new("Echo " + name, "echoes", [new("ask", "ask it")], [], requires?.Connectors is { Length: > 0 } ? [new(Slot, requires.Connectors[0])] : [], Requires: requires);

    private async Task<string> Publish(string name, AppRequirements? requires, CancellationToken ct)
    {
        using var author = await Brain.PersonAsync("author-" + Guid.NewGuid().ToString("N"), ct);
        await Publish(author, name, requires);
        return await PublisherOf(name, ct);
    }

    private async Task<IApp> Publish(Person author, string name, AppRequirements? requires)
    {
        using var _ = author.AsCaller();
        var app = Brain.Get<IApp>(IApp.Key(author.BrainId, name));
        await app.Create(Manifest(name, requires), "## Scenario: Echoes\nIt echoes.");
        await app.BeginBuild(null);
        await app.CompleteBuild(await app.CurrentBuildAttempt(), new() { ["synapses/echo.cs"] = Echo }, null);
        await app.Publish();
        return app;
    }

    private async Task<string> PublisherOf(string name, CancellationToken ct)
    {
        using var caller = User.AsCaller();
        return (await TestWait.UntilAsync(_ => Brain.Get<IAppStore>(IAppStore.Key(WorkspaceId)).List(), entries => entries.Any(entry => entry.Name == name), Patience, ct))
            .First(entry => entry.Name == name).Publisher;
    }

    private const string Echo = """
        using IntoChat.OS.Modules.Apps;
        using IntoChat.OS.Client;
        await using var brain = await IntoChatOSClient.ConnectAsync(args);
        await foreach (var request in brain.On(brain.Get<IApp>(brain.Setting("App")!), "ask", brain.Stopping)) { }
        """;

    private async Task StartDesktop(CancellationToken ct)
    {
        await Brain.Get<IntoChat.OS.Modules.Flutter.Text.IText>(Desktop + "/layout").Set("desktop");
        await Brain.Get<IApp>(Desktop).Configure(new(), []);
        await AppRuntimeReadiness.WaitAsync(Brain, User, ct);
        using var connected = await User.Http.PostAsJsonAsync($"/brains/{WorkspaceId}/apps/connect", new { }, ct);
        connected.EnsureSuccessStatusCode();
    }

    // Opens the Apps window (the dock), then sends `open` as the shell does for a link's target.
    private async Task OpenApps(string target, CancellationToken ct)
    {
        await ClickDesktop("open:apps", ct);
        await TestWait.UntilAsync(_ => Brain.Get<IWorkspace>(WorkspaceId).Read(), state => state.FocusedViewId == "apps", Patience, ct);
        using var sent = await User.Http.PostAsJsonAsync($"/brains/{WorkspaceId}/apps/apps/request", new { operation = "open", input = target }, ct);
        sent.EnsureSuccessStatusCode();
    }

    private async Task Activate(string activation, string target, CancellationToken ct)
    {
        var workspace = Brain.Get<IWorkspace>(WorkspaceId);
        for (var attempt = 0; attempt < 12; attempt++)
        {
            using var response = await User.Http.PostAsJsonAsync($"/brains/{WorkspaceId}/apps/workspace/request", new { operation = "activate", input = activation }, ct);
            response.EnsureSuccessStatusCode();
            try { await TestWait.UntilAsync(_ => workspace.Read(), state => state.Windows.Any(window => window.Tabs.Any(tab => tab.Id == target)), TimeSpan.FromSeconds(10), ct); return; }
            catch (TimeoutException) { }
        }
        throw new TimeoutException("No window opened for " + target + ".");
    }

    private Task<IntoChat.OS.Modules.Flutter.Button.ButtonState> ArmedButton(string key, CancellationToken ct)
        => TestWait.UntilAsync(_ => Brain.Get<IButton>(key).Read(), state => state.Activation is not null, Patience, ct);

    private async Task ClickDesktop(string action, CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            var view = Brain.Get<IView>(Desktop + "/view");
            var published = await TestWait.UntilAsync(_ => view.Read(),
                current => current is not null && Flatten(current.Root).Any(node => node.Kind == UiNodeKind.Button && node.Action == action && node.Enabled), Patience, ct);
            var button = Flatten(published!.Root).First(node => node.Kind == UiNodeKind.Button && node.Action == action);
            try { await view.Activate(published.Generation, button.Key, action, Guid.NewGuid()); return; }
            catch (StaleActionException) when (attempt < 5) { }
        }
    }

    private async Task ClickView(string key, string action, CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            var view = Brain.Get<IView>(Apps + "/view");
            var published = await TestWait.UntilAsync(_ => view.Read(),
                current => current is not null && Flatten(current.Root).Any(node => node.Key == key && node.Action == action && node.Enabled), Patience, ct);
            try { await view.Activate(published!.Generation, key, action, Guid.NewGuid()); return; }
            catch (StaleActionException) when (attempt < 5) { }
        }
    }

    private async Task<UiNode[]> ViewUntil(Func<UiNode[], bool> ready, CancellationToken ct)
    {
        var view = Brain.Get<IView>(Apps + "/view");
        try
        {
            var published = await TestWait.UntilAsync(_ => view.Read(), current => current is not null && ready([.. Flatten(current.Root)]), Patience, ct);
            return [.. Flatten(published!.Root)];
        }
        catch (TimeoutException)
        {
            var last = await view.Read();
            throw new TimeoutException("Apps showed: " + string.Join(", ", last is null ? [] : Flatten(last.Root).Select(node => node.Key + "=" + node.Text)));
        }
    }

    private static IEnumerable<UiNode> Flatten(UiNode root)
    {
        yield return root;
        foreach (var child in root.Children.SelectMany(Flatten)) { yield return child; }
    }
}
