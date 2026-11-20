using DigitalBrain;
using DigitalBrain.Testing;
using DigitalBrain.UI;
using DigitalBrain.Units;
using IntoChat.OS.Apps.Settings;
using IntoChat.OS.Modules.AI.Agents.Context;
using IntoChat.OS.Modules.Apps;
using IntoChat.OS.Modules.UI;
using IntoChat.OS.UI;

namespace IntoChat.OS.Apps.Tests.Unit.Settings;

[DigitalBrain.Neurons.Neuron("approvals"), DigitalBrain.Neurons.StatelessNeuron]
public sealed class FakeApprovalsNeuron : DigitalBrain.Neurons.Neuron, IntoChat.OS.Modules.Apps.Approvals.IApprovals
{
    public static IntoChat.OS.Modules.Apps.Approvals.Approval[] Items { get; set; } = [];

    public Task<IReadOnlyList<IntoChat.OS.Modules.Apps.Approvals.Approval>> Pending(CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<IntoChat.OS.Modules.Apps.Approvals.Approval>>(Items);
}

[DigitalBrain.Neurons.Neuron("ino.synapses"), DigitalBrain.Neurons.StatelessNeuron]
public sealed class InMemoryPortableSynapseStudio : DigitalBrain.Neurons.Neuron, ISynapseStudio
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
        return Task.FromResult(new UnitDeployResult(true, "", DigitalBrain.Units.DeployStatus.Deployed, "hash-1", new Dictionary<string, DigitalBrain.Units.InstallOutcome>()));
    }

    public Task<InoToolResult> Retire(string alias)
    {
        Store.Remove(alias);
        return Task.FromResult(new InoToolResult(""));
    }

    public Task<DigitalBrain.Units.DeployedSource[]> List()
        => Task.FromResult(Store.Keys.Select(alias => new DigitalBrain.Units.DeployedSource(alias, "hash-1", [alias], DigitalBrain.Units.DeployStatus.Deployed, null, DateTimeOffset.UnixEpoch)).ToArray());

    public Task<Dictionary<string, string>> Sidecars(string alias)
        => Task.FromResult(Store.TryGetValue(alias, out var files)
            ? files.Where(kv => !kv.Key.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)).ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal)
            : new Dictionary<string, string>(StringComparer.Ordinal));

    public Task<Dictionary<string, string>> SourceFiles(string alias)
        => Task.FromResult(Store.TryGetValue(alias, out var files)
            ? files.Where(kv => kv.Key.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)).ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal)
            : new Dictionary<string, string>(StringComparer.Ordinal));

    public Task<InoToolResult> Failures(string alias)
        => Task.FromResult(new InoToolResult($"{alias} has no recorded failures.", Ok: true));
}

public sealed class SettingsAppFacts
{
    public static IEnumerable<TheoryDataRow<ScreenOperationCase>> Operations
        => ScreenBaseline.Cases(typeof(SettingsApp), os => os.Register(typeof(AppsModule).Assembly));

    [Theory, MemberData(nameof(Operations))]
    public Task Baseline(ScreenOperationCase operation) => ScreenBaseline.RunAsync(operation);

    [Fact]
    public async Task SettingsBddScenariosPass()
    {
        using var person = IntoChat.OS.Tests.TestEdge.Authority.Enter(IntoChat.OS.Tests.TestEdge.Authority.Person("test", "test", new BrainId("test")));
        await using var os = TestOS.Local();
        os.Register(typeof(AppsModule).Assembly);
        os.Register(typeof(SettingsApp).Assembly);
        var screen = os.Get<IScreen>(new NeuronId(new BrainId("test"), "app.settings", "default"));
        Assert.NotNull(await screen.Read());
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
    public async Task SettingsNavigationAndBackstackTracksHistory()
    {
        await using var os = TestOS.Local().Register(typeof(AppsModule).Assembly);
        var installed = await os.Install<SettingsApp>();
        var app = installed.App;
        Assert.Equal("general", app.State.Section);
        Assert.Null(app.State.SubPage);
        Assert.Null(app.State.Sheet);

        await app.Navigate(new SettingsNavigate("connections", SubPage: "github"));
        Assert.Equal("connections", app.State.Section);
        Assert.Equal("github", app.State.SubPage);
        Assert.Single(app.State.History!);
        Assert.Equal("general", app.State.History![0]);

        await app.Navigate(new SettingsNavigate("models", Sheet: "ollama-config"));
        Assert.Equal("models", app.State.Section);
        Assert.Null(app.State.SubPage);
        Assert.Equal("ollama-config", app.State.Sheet);
        Assert.Equal(2, app.State.History!.Count);
        Assert.Equal("connections/github", app.State.History![1]);

        await app.GoBack(new SettingsGoBack());
        Assert.Equal("connections", app.State.Section);
        Assert.Equal("github", app.State.SubPage);
        Assert.Null(app.State.Sheet);
        Assert.Single(app.State.History!);

        await app.GoBack(new SettingsGoBack());
        Assert.Equal("general", app.State.Section);
        Assert.Null(app.State.SubPage);
        Assert.Empty(app.State.History!);
    }

    [Fact]
    public async Task SettingsInitialStateIncludesDefaultThemeAndAppearance()
    {
        await using var os = TestOS.Local().Register(typeof(AppsModule).Assembly);
        var installed = await os.Install<SettingsApp>();
        var app = installed.App;
        Assert.Equal("auto", app.State.ThemeMode);
        Assert.Equal("cream", app.State.DesktopTone);
        Assert.Equal("English", app.State.Language);
        Assert.Equal("UTC", app.State.TimeZone);
        Assert.Equal("17 Oct 2026", app.State.DateFormat);
        Assert.Equal("fluent-acrylic", app.State.StylePreset);
    }

    [Fact]
    public async Task SettingsRendersAllThirteenSectionsSubPagesAndModalSheetsInCSharp()
    {
        await using var os = TestOS.Local().Register(typeof(AppsModule).Assembly);
        var installed = await os.Install<SettingsApp>();
        var app = installed.App;
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
            await app.Navigate(new SettingsNavigate(section));
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
            await app.Navigate(new SettingsNavigate($"{sec}:{sub}"));
            Assert.Equal(sec, app.State.Section);
            Assert.Equal(sub, app.State.SubPage);
            var json = System.Text.Json.JsonSerializer.Serialize(app.Render(), jsonOptions);
            Assert.Contains(marker, json, StringComparison.Ordinal);
        }

        await app.Navigate(new SettingsNavigate("connections", SubPage: "google_drive", Sheet: "google_drive", SearchQuery: "Ollama"));
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
        using var person = IntoChat.OS.Tests.TestEdge.Authority.Enter(IntoChat.OS.Tests.TestEdge.Authority.Person("local", "local", new BrainId("local")));
        InMemoryPortableSynapseStudio.Store.Clear();
        try
        {
            await using var os = TestOS.Local()
                .Register<UserSettingsNeuron>()
                .Register<ProfileNeuron>()
                .Register<InMemoryPortableSynapseStudio>();

            var settings = await os.Install<SettingsApp>();
            var friends = await os.Install<IntoChat.OS.Apps.Friends.FriendsApp>();

            await os.Get<IntoChat.OS.Modules.Apps.ISynapseStudio>(new DigitalBrain.NeuronId(new DigitalBrain.BrainId("local"), "ino.synapses", "ino/synapses"))
                .DeployFiles("watch.web", IntoChat.OS.Apps.ShippedUnits.Files("watch.web"), new Dictionary<string, string>(StringComparer.Ordinal));
            Assert.Contains("watch.web", InMemoryPortableSynapseStudio.Store.Keys);

            await settings.Request("save-preferences", new SettingsSavePreferences(DisplayName: "Linus", Theme: "dark", DefaultModel: "claude-opus-4-6"));
            await settings.Request("set-variable", new SettingsSetVariable("ANTHROPIC_API_KEY", "vault://ANTHROPIC_API_KEY"));
            await os.Get<IProfile>(new DigitalBrain.NeuronId(new DigitalBrain.BrainId("local"), "os.profile", "profile")).AddFriend("alan-brain", "alan", "Alan Turing", "friends");

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
        using var person = IntoChat.OS.Tests.TestEdge.Authority.Enter(IntoChat.OS.Tests.TestEdge.Authority.Person("local", "local", new BrainId("local")));
        await using var os = TestOS.Local().Register(typeof(AppsModule).Assembly);
        var settings = await os.Install<SettingsApp>();
        var friends = await os.Install<IntoChat.OS.Apps.Friends.FriendsApp>();

        await settings.Request("set-variable", new SettingsSetVariable("OLLAMA_HOST", "http://127.0.0.1:11434"));
        Assert.Equal("variables", settings.App.State.Section);
        Assert.Contains(settings.App.State.Variables!, v => v.Key == "OLLAMA_HOST" && v.Value == "http://127.0.0.1:11434");

        var varJson = System.Text.Json.JsonSerializer.Serialize(settings.App.Render());
        Assert.Contains("OLLAMA_HOST", varJson, StringComparison.Ordinal);

        await os.Get<IProfile>(new DigitalBrain.NeuronId(new DigitalBrain.BrainId("local"), "os.profile", "profile")).AddFriend("grace-brain", "grace", "Grace Hopper", "friends");

        var archive = new PortableBrainSnapshot(
            BrainId: "local",
            ExportedAt: DateTimeOffset.UtcNow,
            Settings: new UserSettingsState("Ada", "light", "gpt-5", [new UserVariableEntry("OLLAMA_HOST", "http://127.0.0.1:11434")]),
            Profile: new ProfileState("ada", "Bio", [], [], "Ada Lovelace", "", [new ProfileFriend("grace-brain", "grace", "Grace Hopper", "friends")]),
            Units: new Dictionary<string, IReadOnlyDictionary<string, string>>(StringComparer.Ordinal));
        await settings.Request("import-brain", new SettingsImportBrain(PortableBrainArchive.ToJson(archive)));

        await settings.Request("open", new SettingsOpen());
        Assert.Contains(settings.App.State.TeamFriends!, f => f.Handle == "grace");

        await settings.App.Navigate(new SettingsNavigate("team"));
        var teamJson = System.Text.Json.JsonSerializer.Serialize(settings.App.Render());
        Assert.Contains("Grace Hopper (@grace)", teamJson, StringComparison.Ordinal);

        await settings.App.Navigate(new SettingsNavigate("keys"));
        var initialKeysJson = System.Text.Json.JsonSerializer.Serialize(settings.App.Render());
        Assert.DoesNotContain("\"Status\":\"saved\"", initialKeysJson, StringComparison.Ordinal);

        await settings.Request("set-variable", new SettingsSetVariable("OPENAI_API_KEY", "vault://OPENAI_API_KEY"));
        await settings.App.Navigate(new SettingsNavigate("keys"));
        var updatedKeysJson = System.Text.Json.JsonSerializer.Serialize(settings.App.Render());
        Assert.Contains("\"Status\":\"saved\"", updatedKeysJson, StringComparison.Ordinal);

        await settings.App.Navigate(new SettingsNavigate("files"));
        var initialFilesJson = System.Text.Json.JsonSerializer.Serialize(settings.App.Render());
        Assert.DoesNotContain("google-drive", initialFilesJson, StringComparison.Ordinal);

        await settings.App.RefreshConnections(new SettingsRefreshConnections("google-drive"));
        await settings.App.Navigate(new SettingsNavigate("files"));
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
            await settings.App.Navigate(new SettingsNavigate("notifications"));

            var wireJson = System.Text.Json.JsonSerializer.Serialize(
                settings.App.Render(),
                DigitalBrain.Invocation.RuntimeProtocol.OptionsFor(typeof(View)));
            Assert.Contains("plan-99", wireJson, StringComparison.Ordinal);
            Assert.Contains("Delete /work/budget.csv?", wireJson, StringComparison.Ordinal);
            Assert.Contains("\"resolve\":null", wireJson, StringComparison.Ordinal);
            Assert.DoesNotContain("\"planId\"", wireJson, StringComparison.OrdinalIgnoreCase);

            await settings.App.Navigate(new SettingsNavigate("keyboard"));
            var keyboardJson = System.Text.Json.JsonSerializer.Serialize(settings.App.Render());
            Assert.DoesNotContain("Ctrl+N", keyboardJson, StringComparison.Ordinal);
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
            IntoChat.OS.UI.ScreenPreview.With<IntoChat.OS.Apps.Ino.InoApp, View>(app => app.Render()),
            IntoChat.OS.UI.ScreenPreview.With<IntoChat.OS.Apps.Ino.InoApp, View>(app => app.RenderSide()),
            IntoChat.OS.UI.ScreenPreview.With<IntoChat.OS.Apps.Desktop.DesktopApp, View>(app => app.Render()),
            IntoChat.OS.UI.ScreenPreview.With<SettingsApp, View>(app => app.Render()),
            IntoChat.OS.UI.ScreenPreview.With<IntoChat.OS.Apps.Usage.UsageApp, View>(app => app.Render()),
            IntoChat.OS.UI.ScreenPreview.With<IntoChat.OS.Apps.Friends.FriendsApp, View>(app => app.Render()),
            IntoChat.OS.UI.ScreenPreview.With<IntoChat.OS.Apps.Data.DataStudioApp, View>(app => app.Render()),
            IntoChat.OS.UI.ScreenPreview.With<IntoChat.OS.Apps.Markdraw.MarkdrawApp, View>(app => app.Render()),
            IntoChat.OS.UI.ScreenPreview.With<IntoChat.OS.Apps.FilmCraft.FilmCraftApp, View>(app => app.Render()),
            IntoChat.OS.UI.ScreenPreview.With<IntoChat.OS.Apps.Tasks.TasksApp, View>(app => app.Render()),
            IntoChat.OS.UI.ScreenPreview.With<IntoChat.OS.Apps.Creator.CreatorApp, View>(app => app.Render()),
            IntoChat.OS.UI.ScreenPreview.With<IntoChat.OS.Apps.Gallery.GalleryApp, View>(app => app.Render()),
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

    [Fact]
    public async Task SettingsRedactedISecretVaultStatusNeverExposesRawSecrets()
    {
        await using var os = TestOS.Local().Register(typeof(AppsModule).Assembly);
        var settings = await os.Install<SettingsApp>();
        var vault = new MemorySecretVault();
        settings.App.SecretVault = vault;

        // Initially no secrets configured
        await settings.App.Navigate(new SettingsNavigate("keys"));
        var initialView = settings.App.Render();
        var initialJson = System.Text.Json.JsonSerializer.Serialize(initialView);
        Assert.Contains("Not set", initialJson, StringComparison.Ordinal);
        Assert.DoesNotContain("\"Status\":\"saved\"", initialJson, StringComparison.Ordinal);

        // Configure ANTHROPIC_API_KEY in vault
        vault.SetSecret("ANTHROPIC_API_KEY");
        await settings.Request("open", new SettingsOpen("keys"));
        await settings.App.Navigate(new SettingsNavigate("keys"));
        var updatedView = settings.App.Render();
        var updatedJson = System.Text.Json.JsonSerializer.Serialize(updatedView);
        Assert.Contains("Configured", updatedJson, StringComparison.Ordinal);
        Assert.Contains("\"Status\":\"saved\"", updatedJson, StringComparison.Ordinal);

        // Verify variables section redacts secret values
        await settings.Request("set-variable", new SettingsSetVariable("SERVICE_SECRET_KEY", "sensitive-secret-token"));
        await settings.App.Navigate(new SettingsNavigate("variables"));
        var varJson = System.Text.Json.JsonSerializer.Serialize(settings.App.Render());
        Assert.Contains("SERVICE_SECRET_KEY", varJson, StringComparison.Ordinal);
        Assert.DoesNotContain("sensitive-secret-token", varJson, StringComparison.Ordinal);
        Assert.Contains("Configured", varJson, StringComparison.Ordinal);
    }

    [Fact]
    public async Task BrainPreferenceStorePersistsPreferencesAcrossReload()
    {
        using var person = IntoChat.OS.Tests.TestEdge.Authority.Enter(IntoChat.OS.Tests.TestEdge.Authority.Person("local", "local", new BrainId("local")));
        await using var os = TestOS.Local().Register(typeof(AppsModule).Assembly);
        var settings = await os.Install<SettingsApp>();
        var userSettings = os.Get<IUserSettings>(new NeuronId(new BrainId("local"), "os.settings", "settings"));
        var store = new BrainPreferenceStore(userSettings);
        settings.App.PreferenceStore = store;

        await settings.Request("save-preferences", new SettingsSavePreferences(
            DisplayName: "Ada Lovelace",
            Theme: "dark",
            DefaultModel: "claude-sonnet-4-6",
            StylePreset: "cyber-synaptic",
            BarLayout: "compact-pill",
            ThemeMode: "dark",
            DesktopTone: "sand",
            Language: "Norwegian",
            TimeZone: "Europe/Oslo",
            DateFormat: "dd.MM.yyyy"));

        Assert.Equal("Ada Lovelace", settings.App.State.DisplayName);
        Assert.Equal("dark", settings.App.State.Theme);
        Assert.Equal("claude-sonnet-4-6", settings.App.State.DefaultModel);
        Assert.Equal("cyber-synaptic", settings.App.State.StylePreset);
        Assert.Equal("compact-pill", settings.App.State.BarLayout);

        // Load directly through store to verify persistence
        var loaded = await store.LoadAsync(TestContext.Current.CancellationToken);
        Assert.Equal("Ada Lovelace", loaded.DisplayName);
        Assert.Equal("dark", loaded.Theme);
        Assert.Equal("claude-sonnet-4-6", loaded.DefaultModel);
        Assert.Equal("cyber-synaptic", loaded.StylePreset);
        Assert.Equal("compact-pill", loaded.BarLayout);
        Assert.Equal("Europe/Oslo", loaded.TimeZone);
    }

    [Fact]
    public async Task EveryIdOfferedToAUserIsInTheCatalogue()
    {
        await using var os = TestOS.Local().Register(typeof(AppsModule).Assembly);
        var settings = await os.Install<SettingsApp>();
        await settings.App.Navigate(new SettingsNavigate("models"));

        var json = System.Text.Json.JsonSerializer.Serialize(settings.App.Render());
        using var doc = System.Text.Json.JsonDocument.Parse(json);

        var offeredModels = new List<string>();
        CollectOptionValues(doc.RootElement, offeredModels);

        Assert.NotEmpty(offeredModels);
        var catalogIds = IntoChat.OS.Modules.AI.Models.LLMModel.All.Select(m => m.Id).ToHashSet(StringComparer.Ordinal);
        Assert.All(offeredModels, id => Assert.True(catalogIds.Contains(id), $"Uncatalogued model '{id}' offered in Settings."));
        Assert.DoesNotContain("claude-sonnet-4-5", offeredModels);
        Assert.DoesNotContain("gpt-5", offeredModels);

        static void CollectOptionValues(System.Text.Json.JsonElement element, List<string> values)
        {
            if (element.ValueKind == System.Text.Json.JsonValueKind.Object)
            {
                if (TryGetPropertyIgnoreCase(element, "options", out var options)
                    && options.ValueKind == System.Text.Json.JsonValueKind.Array)
                {
                    foreach (var opt in options.EnumerateArray())
                    {
                        if (opt.ValueKind == System.Text.Json.JsonValueKind.Object
                            && TryGetPropertyIgnoreCase(opt, "id", out var idProp)
                            && idProp.GetString() is { Length: > 0 } id)
                        {
                            values.Add(id);
                        }
                    }
                }
                foreach (var prop in element.EnumerateObject())
                {
                    CollectOptionValues(prop.Value, values);
                }
            }
            else if (element.ValueKind == System.Text.Json.JsonValueKind.Array)
            {
                foreach (var item in element.EnumerateArray())
                {
                    CollectOptionValues(item, values);
                }
            }
        }

        static bool TryGetPropertyIgnoreCase(System.Text.Json.JsonElement obj, string name, out System.Text.Json.JsonElement value)
        {
            foreach (var prop in obj.EnumerateObject())
            {
                if (string.Equals(prop.Name, name, StringComparison.OrdinalIgnoreCase))
                {
                    value = prop.Value;
                    return true;
                }
            }
            value = default;
            return false;
        }
    }

    [Fact]
    public void SettingsAppState_AggregateExplorer_UnfoldsHealthyStateGraphAndRuleGuards()
    {
        var graph = AggregateExplorer<SettingsAppState>.ExploreScreen<SettingsApp>(maxStates: 48).EnsureHealthy();

        Assert.True(graph.IsHealthy, string.Join("\n", graph.Violations));
        Assert.Empty(graph.Violations);
        Assert.Contains(graph.States, s => s.State.Section == "general");
        Assert.Contains(graph.States, s => s.State.Section == "models");
        Assert.Contains(graph.States, s => s.State.Section == "billing");
        Assert.Contains(graph.States, s => s.State.Section == "connections");
        Assert.Contains(graph.States, s => s.State.Section == "variables");

        Assert.Contains(graph.RejectedTransitions, t => t.BrokenRules.Contains("invalid_section", StringComparer.Ordinal));
        Assert.Contains(graph.RejectedTransitions, t => t.BrokenRules.Contains("can_go_back", StringComparer.Ordinal));
        Assert.Contains(graph.RejectedTransitions, t => t.BrokenRules.Contains("invalid_display_name", StringComparer.Ordinal));
        Assert.Contains(graph.RejectedTransitions, t => t.BrokenRules.Contains("invalid_theme", StringComparer.Ordinal));
        Assert.Contains(graph.RejectedTransitions, t => t.BrokenRules.Contains("valid_monthly_budget", StringComparer.Ordinal));
        Assert.Contains(graph.RejectedTransitions, t => t.BrokenRules.Contains("invalid_connection_key", StringComparer.Ordinal));
        Assert.Contains(graph.RejectedTransitions, t => t.BrokenRules.Contains("variable_key_required", StringComparer.Ordinal));

        var dot = graph.ToDot();
        Assert.Contains("digraph SettingsAppState", dot, StringComparison.Ordinal);
        var scene = Kit.StateGraphView(graph);
        Assert.Equal(graph.States.Count, scene.Nodes.Count);
    }

    [Fact]
    public async Task SettingsDomainRules_RefuseInvalidSectionsThemesBudgetsConnectionsVariablesAndMissingArchives()
    {
        var initial = new SettingsAppState();

        var badSection = initial.OpenSection("nonexistent-tab");
        Assert.True(badSection.IsRefused);
        Assert.Contains("invalid_section", badSection.Rejection!.Rules);

        var cannotReturn = initial.ReturnPrevious();
        Assert.True(cannotReturn.IsRefused);
        Assert.Contains("can_go_back", cannotReturn.Rejection!.Rules);

        var moved = initial.OpenSection("connections", "github");
        Assert.True(moved.IsAccepted);
        var returned = moved.Next!.ReturnPrevious();
        Assert.True(returned.IsAccepted);
        Assert.Equal("general", returned.Next!.Section);

        var badPrefs = initial.ApplyPreferences(
            displayName: "   ",
            theme: "solarized",
            stylePreset: "unknown-preset",
            barLayout: "unknown-bar",
            themeMode: "invalid-mode",
            desktopTone: "neon");
        Assert.True(badPrefs.IsRefused);
        Assert.Contains("invalid_display_name", badPrefs.Rejection!.Rules);
        Assert.Contains("invalid_theme", badPrefs.Rejection!.Rules);
        Assert.Contains("invalid_style_preset", badPrefs.Rejection.Rules);
        Assert.Contains("invalid_bar_layout", badPrefs.Rejection.Rules);
        Assert.Contains("invalid_theme_mode", badPrefs.Rejection.Rules);
        Assert.Contains("invalid_desktop_tone", badPrefs.Rejection.Rules);

        var badConn = initial.RegisterConnection("   ");
        Assert.True(badConn.IsRefused);
        Assert.Contains("invalid_connection_key", badConn.Rejection!.Rules);

        var badVar = initial.UpsertVariable("BAD KEY WITH SPACES", "1");
        Assert.True(badVar.IsRefused);
        Assert.Contains("variable_key_required", badVar.Rejection!.Rules);

        await using var os = TestOS.Local().Register(typeof(AppsModule).Assembly);
        var settings = await os.Install<SettingsApp>();
        using var person = os.Edge.Enter(os.Edge.Person("local", "local", new BrainId("local")));
        Assert.NotNull(await settings.Screen.Open());

        await settings.Request("save-preferences", new SettingsSavePreferences(DisplayName: "   ", Theme: "neon"));
        Assert.NotNull(settings.App.LastRejection);
        Assert.Contains("invalid_display_name", settings.App.LastRejection!.Rules);
        Assert.Contains("invalid_theme", settings.App.LastRejection.Rules);
        Assert.Equal("User", settings.App.State.DisplayName);
        Assert.Equal("dark", settings.App.State.Theme);

        await settings.Request("set-budget", new SettingsSetBudget(0));
        Assert.NotNull(settings.App.LastRejection);
        Assert.Contains("valid_monthly_budget", settings.App.LastRejection!.Rules);

        await settings.Request("set-variable", new SettingsSetVariable("   ", "val"));
        Assert.NotNull(settings.App.LastRejection);
        Assert.Contains("variable_key_required", settings.App.LastRejection!.Rules);

        await settings.Request("import-brain", new SettingsImportBrain(""));
        Assert.NotNull(settings.App.LastRejection);
        Assert.Contains("archive_required", settings.App.LastRejection!.Rules);

        var renderedJson = System.Text.Json.JsonSerializer.Serialize(settings.App.Render());
        Assert.Contains("Invalid setting value", renderedJson, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SettingsApp_UiIntrospector_UnfoldsCleanAccordantStateGraphAndDot()
    {
        var brain = new BrainId("settings-introspect");
        using var person = IntoChat.OS.Tests.TestEdge.Authority.Enter(IntoChat.OS.Tests.TestEdge.Authority.Person("owner", "owner", brain));
        await using var os = TestOS.Local();
        os.Register(typeof(UiModule).Assembly);
        os.Register<AgentContextNeuron>();
        os.Register<UiIntrospectorNeuron>();

        var introspector = os.Get<IUiIntrospector>(IUiIntrospector.NeuronIdOf(brain));
        var snapshot = await introspector.ExploreStateGraph(typeof(SettingsApp).FullName!, maxStates: 48);

        Assert.True(snapshot.IsHealthy, string.Join("\n", snapshot.Violations));
        Assert.Empty(snapshot.Violations);
        Assert.True(snapshot.States.Length >= 6);
        Assert.Contains(snapshot.Transitions, t => t.Accepted);
        Assert.Contains(snapshot.Transitions, t => !t.Accepted && t.BrokenRules.Contains("valid_monthly_budget", StringComparer.Ordinal));
        Assert.Contains(snapshot.Transitions, t => !t.Accepted && t.BrokenRules.Contains("invalid_section", StringComparer.Ordinal));
        Assert.Contains("digraph SettingsAppState", snapshot.Dot, StringComparison.Ordinal);

        var report = await introspector.SyncContext();
        Assert.Contains("SettingsApp", report.ContextSummary, StringComparison.Ordinal);
    }
}
