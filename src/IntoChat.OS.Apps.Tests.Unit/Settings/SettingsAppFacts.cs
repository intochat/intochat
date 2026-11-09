using DigitalBrain;
using DigitalBrain.Testing;
using DigitalBrain.UI;
using IntoChat.OS.Apps.Settings;
using IntoChat.OS.Modules.Apps;

namespace IntoChat.OS.Apps.Tests.Unit.Settings;

[DigitalBrain.Neuron("approvals"), DigitalBrain.StatelessNeuron]
public sealed class FakeApprovalsNeuron : DigitalBrain.Neuron, IntoChat.OS.Modules.Apps.Approvals.IApprovals
{
    public static IntoChat.OS.Modules.Apps.Approvals.Approval[] Items { get; set; } = [];

    public Task<IReadOnlyList<IntoChat.OS.Modules.Apps.Approvals.Approval>> Pending(CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<IntoChat.OS.Modules.Apps.Approvals.Approval>>(Items);
}

[DigitalBrain.Neuron("ino.synapses"), DigitalBrain.StatelessNeuron]
public sealed class InMemoryPortableSynapseStudio : DigitalBrain.Neuron, ISynapseStudio
{
    public static Dictionary<string, Dictionary<string, string>> Store { get; } = new(StringComparer.Ordinal);

    public Task<InoToolResult> Check(string source) => Task.FromResult(new InoToolResult(""));

    public Task<InoToolResult> Deploy(string alias, string source)
    {
        Store[alias] = new Dictionary<string, string>(StringComparer.Ordinal) { [$"{alias}.cs"] = source };
        return Task.FromResult(new InoToolResult(""));
    }

    public Task<UnitDeployResult> DeployFiles(string alias, IReadOnlyDictionary<string, string> files, IReadOnlyDictionary<string, string> bindings)
    {
        Store[alias] = new Dictionary<string, string>(files, StringComparer.Ordinal);
        return Task.FromResult(new UnitDeployResult(true, "", DigitalBrain.DeployStatus.Deployed, "hash-1", new Dictionary<string, DigitalBrain.InstallOutcome>()));
    }

    public Task<InoToolResult> Retire(string alias)
    {
        Store.Remove(alias);
        return Task.FromResult(new InoToolResult(""));
    }

    public Task<DigitalBrain.DeployedSource[]> List()
        => Task.FromResult(Store.Keys.Select(alias => new DigitalBrain.DeployedSource(alias, "hash-1", [alias], DigitalBrain.DeployStatus.Deployed, null, DateTimeOffset.UnixEpoch)).ToArray());

    public Task<Dictionary<string, string>> Sidecars(string alias)
        => Task.FromResult(Store.TryGetValue(alias, out var files)
            ? files.Where(kv => !kv.Key.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)).ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal)
            : new Dictionary<string, string>(StringComparer.Ordinal));

    public Task<Dictionary<string, string>> SourceFiles(string alias)
        => Task.FromResult(Store.TryGetValue(alias, out var files)
            ? files.Where(kv => kv.Key.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)).ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal)
            : new Dictionary<string, string>(StringComparer.Ordinal));
}

public sealed class SettingsAppFacts
{
    public static IEnumerable<TheoryDataRow<AppOperationCase>> Operations
        => AppBaseline.Cases(typeof(SettingsApp), os => os.Register(typeof(AppsModule).Assembly));

    [Theory, MemberData(nameof(Operations))]
    public Task Baseline(AppOperationCase operation) => AppBaseline.RunAsync(operation);

    [Fact]
    public async Task SettingsBddScenariosPass()
    {
        var report = await BddRunner.VerifyAsync<SettingsApp>(
            fakes: os => os.Register(typeof(AppsModule).Assembly),
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(report.Passed);
        Assert.Single(report.Scenarios);
    }

    [Fact]
    public void SettingsDefinitionMetadataMatchesContract()
    {
        var definition = SettingsApp.Definition;
        Assert.Equal("settings", definition.Id);
        Assert.Equal("#64748B", definition.Accent);
        Assert.Equal("System", definition.Category);
        Assert.Equal("SettingsApp", definition.Name);
        Assert.Equal(typeof(SettingsApp), definition.AppType);
    }

    [Fact]
    public void SettingsCarriesTheFlutterTreesAndTheAppsLibrary()
    {
        var bundle = SettingsApp.Bundle;
        Assert.Equal("settings", bundle.Name);
        Assert.Equal("2.1.0", bundle.Version);
        Assert.Equal(typeof(SettingsApp).AssemblyQualifiedName, bundle.CompiledType);
        Assert.Equal("Settings", bundle.Manifest.Title);
        Assert.Contains(bundle.Manifest.Operations, op => op.Name == "open");
    }

    [Fact]
    public void SettingsNavigationAndBackstackTracksHistory()
    {
        var app = new SettingsApp();
        Assert.Equal("general", app.State.Section);
        Assert.Null(app.State.SubPage);
        Assert.Null(app.State.Sheet);

        app.Navigate(new SettingsNavigate("connections", SubPage: "github"));
        Assert.Equal("connections", app.State.Section);
        Assert.Equal("github", app.State.SubPage);
        Assert.Single(app.State.History!);
        Assert.Equal("general", app.State.History![0]);

        app.Navigate(new SettingsNavigate("models", Sheet: "ollama-config"));
        Assert.Equal("models", app.State.Section);
        Assert.Null(app.State.SubPage);
        Assert.Equal("ollama-config", app.State.Sheet);
        Assert.Equal(2, app.State.History!.Count);
        Assert.Equal("connections/github", app.State.History![1]);

        app.GoBack(new SettingsGoBack());
        Assert.Equal("connections", app.State.Section);
        Assert.Equal("github", app.State.SubPage);
        Assert.Null(app.State.Sheet);
        Assert.Single(app.State.History!);

        app.GoBack(new SettingsGoBack());
        Assert.Equal("general", app.State.Section);
        Assert.Null(app.State.SubPage);
        Assert.Empty(app.State.History!);
    }

    [Fact]
    public void SettingsInitialStateIncludesDefaultThemeAndAppearance()
    {
        var app = new SettingsApp();
        Assert.Equal("auto", app.State.ThemeMode);
        Assert.Equal("cream", app.State.DesktopTone);
        Assert.Equal("English", app.State.Language);
        Assert.Equal("UTC", app.State.TimeZone);
        Assert.Equal("17 Oct 2026", app.State.DateFormat);
        Assert.Equal("fluent-acrylic", app.State.StylePreset);
    }

    [Fact]
    public void SettingsRendersAllThirteenSectionsSubPagesAndModalSheetsInCSharp()
    {
        var app = new SettingsApp();
        string[] sections =
        [
            "general", "desktop", "apps", "connections", "files",
            "models", "keys", "variables", "billing", "team",
            "notifications", "keyboard", "danger",
        ];

        var jsonOptions = new System.Text.Json.JsonSerializerOptions
        {
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        };
        Assert.Equal(13, SettingsApp.SearchIndex.Count);
        Assert.Contains(SettingsApp.SearchIndex, s => s.ActionUri == "os://window/secure");
        Assert.Contains(SettingsApp.SearchIndex, s => s.ActionUri == "os://window/profile");

        foreach (var section in sections)
        {
            app.Navigate(new SettingsNavigate(section));
            Assert.Equal(section, app.State.Section);
            var view = app.Render();
            var json = System.Text.Json.JsonSerializer.Serialize(view, jsonOptions);
            Assert.Contains(SettingsApp.SearchIndex.Single(s => s.SectionId == section).Title, json, StringComparison.Ordinal);
        }

        (string Section, string SubPage, string ExpectedMarker)[] subPages =
        [
            ("apps", "ino", "AppPage · ino"),
            ("apps", "memory", "Ino Memory Store"),
            ("connections", "google_drive", "ConnectionPage · google_drive"),
            ("team", "astrid", "PersonPage · astrid"),
            ("models", "local-server", "Ollama Local Server"),
        ];

        foreach (var (sec, sub, marker) in subPages)
        {
            app.Navigate(new SettingsNavigate($"{sec}:{sub}"));
            Assert.Equal(sec, app.State.Section);
            Assert.Equal(sub, app.State.SubPage);
            var json = System.Text.Json.JsonSerializer.Serialize(app.Render(), jsonOptions);
            Assert.Contains(marker, json, StringComparison.Ordinal);
        }

        app.Navigate(new SettingsNavigate("connections", SubPage: "google_drive", Sheet: "google_drive", SearchQuery: "Ollama"));
        Assert.Equal("google_drive", app.State.Sheet);
        var sheetJson = System.Text.Json.JsonSerializer.Serialize(app.Render(), jsonOptions);
        Assert.Contains("Sheet · google_drive", sheetJson, StringComparison.Ordinal);

        var filtered = SettingsApp.FilterSections("Ollama");
        Assert.Single(filtered);
        Assert.Equal("models", filtered[0].SectionId);
    }

    [Fact]
    public async Task SettingsExportAndImportBrainArchiveRestoresSettingsAndUnits()
    {
        await using var os = TestOS.Local().Register(typeof(AppsModule).Assembly);
        var settings = await os.Install<SettingsApp>();

        await settings.Request("save-preferences", new SettingsSavePreferences(DisplayName: "Ada", Theme: "light", DefaultModel: "gpt-5"));
        await settings.Request("export-brain", new SettingsExportBrain());

        Assert.Equal("danger", settings.App.State.Section);
        Assert.Equal("export-archive", settings.App.State.Sheet);
        var archiveJson = settings.App.State.ExportedArchiveJson;
        Assert.False(string.IsNullOrWhiteSpace(archiveJson));

        await settings.Request("save-preferences", new SettingsSavePreferences(DisplayName: "Mutated", Theme: "dark", DefaultModel: "qwen3.5:4b"));
        Assert.Equal("Mutated", settings.App.State.DisplayName);

        await settings.Request("import-brain", new SettingsImportBrain(archiveJson));
        Assert.Equal("Ada", settings.App.State.DisplayName);
        Assert.Equal("light", settings.App.State.Theme);
        Assert.Equal("gpt-5", settings.App.State.DefaultModel);
    }

    [Fact]
    public async Task ExportAndImportBrainRoundTripsSeededUnitsVariablesAndFriendsAcrossReset()
    {
        InMemoryPortableSynapseStudio.Store.Clear();
        try
        {
            await using var os = TestOS.Local()
                .Register<UserSettingsNeuron>()
                .Register<ProfileNeuron>()
                .Register<InMemoryPortableSynapseStudio>();

            var creator = await os.Install<IntoChat.OS.Apps.Creator.CreatorApp>();
            var settings = await os.Install<SettingsApp>();
            var friends = await os.Install<IntoChat.OS.Apps.Friends.FriendsApp>();

            await creator.Request("seed", new IntoChat.OS.Apps.Creator.CreatorSeed("watch.web"));
            Assert.Contains("watch.web", InMemoryPortableSynapseStudio.Store.Keys);

            await settings.Request("save-preferences", new SettingsSavePreferences(DisplayName: "Linus", Theme: "dark", DefaultModel: "claude-opus-4-6"));
            await settings.Request("set-variable", new SettingsSetVariable("ANTHROPIC_API_KEY", "vault://ANTHROPIC_API_KEY"));
            await friends.Request("add-friend", new IntoChat.OS.Apps.Friends.FriendsAddFriend("alan-brain", "alan", "Alan Turing", "friends"));

            await settings.Request("export-brain", new SettingsExportBrain());
            var archiveJson = settings.App.State.ExportedArchiveJson;
            Assert.False(string.IsNullOrWhiteSpace(archiveJson));
            Assert.Contains("watch.web", archiveJson, StringComparison.Ordinal);
            Assert.Contains("vault://ANTHROPIC_API_KEY", archiveJson, StringComparison.Ordinal);
            Assert.Contains("Alan Turing", archiveJson, StringComparison.Ordinal);

            // Simulate wiping deployed units and mutating settings before restoring the portable .brain archive.
            InMemoryPortableSynapseStudio.Store.Clear();
            await settings.Request("save-preferences", new SettingsSavePreferences(DisplayName: "Wiped", Theme: "light", DefaultModel: "qwen3.5:4b"));
            Assert.Empty(InMemoryPortableSynapseStudio.Store);

            await settings.Request("import-brain", new SettingsImportBrain(archiveJson));

            Assert.Equal("Linus", settings.App.State.DisplayName);
            Assert.Equal("dark", settings.App.State.Theme);
            Assert.Equal("claude-opus-4-6", settings.App.State.DefaultModel);
            Assert.Contains(settings.App.State.Variables!, v => v.Key == "ANTHROPIC_API_KEY" && v.Value == "vault://ANTHROPIC_API_KEY");
            Assert.Contains(settings.App.State.TeamFriends!, f => f.Handle == "alan" && f.DisplayName == "Alan Turing");
            Assert.Contains("watch.web", settings.App.State.DeployedUnits!);
            Assert.Contains("WebWatch.cs", InMemoryPortableSynapseStudio.Store["watch.web"].Keys);
        }
        finally
        {
            InMemoryPortableSynapseStudio.Store.Clear();
        }
    }

    [Fact]
    public async Task SettingsProjectsVariablesDeployedUnitsAndTeamFriendsFromLiveNeurons()
    {
        await using var os = TestOS.Local().Register(typeof(AppsModule).Assembly);
        var settings = await os.Install<SettingsApp>();
        var friends = await os.Install<IntoChat.OS.Apps.Friends.FriendsApp>();

        await settings.Request("set-variable", new SettingsSetVariable("OLLAMA_HOST", "http://127.0.0.1:11434"));
        Assert.Equal("variables", settings.App.State.Section);
        Assert.Contains(settings.App.State.Variables!, v => v.Key == "OLLAMA_HOST" && v.Value == "http://127.0.0.1:11434");

        var varJson = System.Text.Json.JsonSerializer.Serialize(settings.App.Render());
        Assert.Contains("OLLAMA_HOST", varJson, StringComparison.Ordinal);

        await friends.Request("add-friend", new IntoChat.OS.Apps.Friends.FriendsAddFriend("grace-brain", "grace", "Grace Hopper", "friends"));

        var archive = new PortableBrainSnapshot(
            BrainId: "local",
            ExportedAt: DateTimeOffset.UtcNow,
            Settings: new UserSettingsState("Ada", "light", "gpt-5", [new UserVariableEntry("OLLAMA_HOST", "http://127.0.0.1:11434")]),
            Profile: new ProfileState("ada", "Bio", [], [], "Ada Lovelace", "", [new ProfileFriend("grace-brain", "grace", "Grace Hopper", "friends")]),
            Units: new Dictionary<string, IReadOnlyDictionary<string, string>>(StringComparer.Ordinal));
        await settings.Request("import-brain", new SettingsImportBrain(PortableBrainArchive.ToJson(archive)));

        await settings.Request("open", new SettingsOpen());
        Assert.Contains(settings.App.State.TeamFriends!, f => f.Handle == "grace");

        settings.App.Navigate(new SettingsNavigate("team"));
        var teamJson = System.Text.Json.JsonSerializer.Serialize(settings.App.Render());
        Assert.Contains("Grace Hopper (@grace)", teamJson, StringComparison.Ordinal);

        settings.App.Navigate(new SettingsNavigate("keys"));
        var initialKeysJson = System.Text.Json.JsonSerializer.Serialize(settings.App.Render());
        Assert.DoesNotContain("\"Status\":\"saved\"", initialKeysJson, StringComparison.Ordinal);

        await settings.Request("set-variable", new SettingsSetVariable("OPENAI_API_KEY", "vault://OPENAI_API_KEY"));
        settings.App.Navigate(new SettingsNavigate("keys"));
        var updatedKeysJson = System.Text.Json.JsonSerializer.Serialize(settings.App.Render());
        Assert.Contains("\"Status\":\"saved\"", updatedKeysJson, StringComparison.Ordinal);

        settings.App.Navigate(new SettingsNavigate("files"));
        var initialFilesJson = System.Text.Json.JsonSerializer.Serialize(settings.App.Render());
        Assert.DoesNotContain("google-drive", initialFilesJson, StringComparison.Ordinal);

        settings.App.RefreshConnections(new SettingsRefreshConnections("google-drive"));
        settings.App.Navigate(new SettingsNavigate("files"));
        var updatedFilesJson = System.Text.Json.JsonSerializer.Serialize(settings.App.Render());
        Assert.Contains("Connected Mount 1", updatedFilesJson, StringComparison.Ordinal);
        Assert.Contains("google-drive", updatedFilesJson, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SettingsSurfacesPendingApprovalsInNotificationsSection()
    {
        FakeApprovalsNeuron.Items =
        [
            new IntoChat.OS.Modules.Apps.Approvals.Approval(
                "plan-99",
                IntoChat.OS.Modules.Apps.Approvals.ApprovalKind.StorageWrite,
                "/work/budget.csv",
                "Delete /work/budget.csv?",
                "Storage write requires confirmation.",
                DateTimeOffset.UnixEpoch,
                DateTimeOffset.UnixEpoch.AddHours(1),
                ""),
        ];

        try
        {
            await using var os = TestOS.Local().Register<UserSettingsNeuron>().Register<FakeApprovalsNeuron>();
            var settings = await os.Install<SettingsApp>();
            await settings.Request("open", new SettingsOpen());
            settings.App.Navigate(new SettingsNavigate("notifications"));

            var notificationsJson = System.Text.Json.JsonSerializer.Serialize(settings.App.Render());
            Assert.Contains("plan-99", notificationsJson, StringComparison.Ordinal);
            Assert.Contains("Delete /work/budget.csv?", notificationsJson, StringComparison.Ordinal);
        }
        finally
        {
            FakeApprovalsNeuron.Items = [];
        }
    }

    [Fact]
    public void ShippedAppsInitialViewsContainZeroFakeDemoData()
    {
        var jsonOptions = new System.Text.Json.JsonSerializerOptions
        {
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        };

        View[] initialViews =
        [
            new IntoChat.OS.Apps.Ino.InoApp().Render(),
            new IntoChat.OS.Apps.Ino.InoApp().RenderSide(),
            new IntoChat.OS.Apps.Desktop.DesktopApp().Render(),
            new SettingsApp().Render(),
            new SettingsApp().RenderSide(),
            new IntoChat.OS.Apps.Usage.UsageApp().Render(),
            new IntoChat.OS.Apps.Friends.FriendsApp().Render(),
            new IntoChat.OS.Apps.Friends.FriendsApp().RenderSide(),
            new IntoChat.OS.Apps.Data.DataStudioApp().Render(),
            new IntoChat.OS.Apps.Data.DataStudioApp().RenderSide(),
            new IntoChat.OS.Apps.Markdraw.MarkdrawApp().Render(),
            new IntoChat.OS.Apps.Markdraw.MarkdrawApp().RenderSide(),
            new IntoChat.OS.Apps.FilmCraft.FilmCraftApp().Render(),
            new IntoChat.OS.Apps.Tasks.TasksApp().Render(),
            new IntoChat.OS.Apps.Creator.CreatorApp().Render(),
            new IntoChat.OS.Apps.Gallery.GalleryApp().Render(),
            new IntoChat.OS.Apps.Gallery.GalleryApp().RenderSide(),
        ];

        Assert.DoesNotContain("Nordlys", IntoChat.OS.Apps.Markdraw.MarkdrawApp.DefaultPipelineScene, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(IntoChat.OS.Apps.Desktop.DesktopApp.NativeCatalog, item => item.Id == "usage");

        string[] forbiddenDemoTokens = ["Nordlys", "Havbris", "Kari Holm", "$420,000", "42.80", "1,250,000"];

        foreach (var view in initialViews)
        {
            var json = System.Text.Json.JsonSerializer.Serialize(view, jsonOptions);
            foreach (var token in forbiddenDemoTokens)
            {
                Assert.DoesNotContain(token, json, StringComparison.OrdinalIgnoreCase);
            }
        }
    }
}
