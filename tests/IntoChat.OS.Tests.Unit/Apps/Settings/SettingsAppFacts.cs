using DigitalBrain;
using DigitalBrain.Identity.Local.Machine;
using DigitalBrain.Testing;
using DigitalBrain.UI;
using DigitalBrain.UI.Commands;
using DigitalBrain.Units;
using IntoChat.OS.Apps.Settings;
using IntoChat.OS.Modules.AI.Agents.Context;
using IntoChat.OS.Modules.AI.Models;
using IntoChat.OS.Modules.Apps;
using IntoChat.OS.Modules.Devices;
using IntoChat.OS.Modules.UI;
using IntoChat.OS.UI;
using Microsoft.Extensions.DependencyInjection;

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

            await settings.Request("export-brain", new SettingsExportBrain());
            var archiveJson = settings.App.State.ExportedArchiveJson;
            Assert.False(string.IsNullOrWhiteSpace(archiveJson));
            Assert.Contains("watch.web", archiveJson, StringComparison.Ordinal);
            Assert.Contains("vault://ANTHROPIC_API_KEY", archiveJson, StringComparison.Ordinal);

            // Simulate wiping deployed units and mutating settings before restoring the portable .brain archive.
            InMemoryPortableSynapseStudio.Store.Clear();
            await settings.Request("save-preferences", new SettingsSavePreferences(DisplayName: "Wiped", Theme: "light", DefaultModel: "qwen3.5:4b"));
            Assert.Empty(InMemoryPortableSynapseStudio.Store);

            await settings.Request("import-brain", new SettingsImportBrain(archiveJson));

            Assert.Equal("Linus", settings.App.State.DisplayName);
            Assert.Equal("dark", settings.App.State.Theme);
            Assert.Equal("claude-opus-4-6", settings.App.State.DefaultModel);
            Assert.Contains(settings.App.State.Variables!, v => v.Key == "ANTHROPIC_API_KEY" && v.Value == "vault://ANTHROPIC_API_KEY");
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
        os.SeedStoredState(
            FriendsDefaults.Id("local"),
            """{"pending":[],"refused":0,"friends":[{"account":"grace","brain":"grace-brain","name":"Grace Hopper","since":"2026-01-01T00:00:00+00:00"}]}""");
        var settings = await os.Install<SettingsApp>();
        var friends = await os.Install<IntoChat.OS.Apps.Friends.FriendsApp>();

        await settings.Request("set-variable", new SettingsSetVariable("OLLAMA_HOST", "http://127.0.0.1:11434"));
        Assert.Equal("variables", settings.App.State.Section);
        Assert.Contains(settings.App.State.Variables!, v => v.Key == "OLLAMA_HOST" && v.Value == "http://127.0.0.1:11434");

        var varJson = System.Text.Json.JsonSerializer.Serialize(settings.App.Render());
        Assert.Contains("OLLAMA_HOST", varJson, StringComparison.Ordinal);

        var archive = new PortableBrainSnapshot(
            BrainId: "local",
            ExportedAt: DateTimeOffset.UtcNow,
            Settings: new UserSettingsState("Ada", "light", "gpt-5", [new UserVariableEntry("OLLAMA_HOST", "http://127.0.0.1:11434")]),
            Profile: new ProfileState("ada", "Bio", [], [], "Ada Lovelace", "", []),
            Units: new Dictionary<string, IReadOnlyDictionary<string, string>>(StringComparer.Ordinal));
        await settings.Request("import-brain", new SettingsImportBrain(PortableBrainArchive.ToJson(archive)));

        await settings.Request("open", new SettingsOpen());
        Assert.Contains(settings.App.State.TeamRoster!, f => f.Account.Value == "grace");

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
        var catalogIds = IntoChat.OS.Modules.AI.Models.KnownModels.All.Select(m => m.Id.Value).ToHashSet(StringComparer.Ordinal);
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

    [Fact]
    public async Task SettingsShowsTheHardwareLineRuntimesAndInstallQueue()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"intochat-settings-device-{Guid.NewGuid():N}");
        var machine = new MachineId(Path.Combine(tempDir, "machine-id"));
        var services = new ServiceCollection().AddSingleton(machine).BuildServiceProvider();

        FakeSettingsDevice.Reset(
            machine.Value,
            new DeviceView(
                machine.Value,
                new Hardware(32L * 1024 * 1024 * 1024, 12L * 1024 * 1024 * 1024, [Accelerator.Cpu, Accelerator.Cuda]),
                [
                    new RuntimeStatus(RuntimeId.Ollama, true, "0.9.0", new Uri("http://127.0.0.1:11434")),
                    new RuntimeStatus(RuntimeId.FoundryLocal, true, "2.5.0", new Uri("http://127.0.0.1:5272")),
                    new RuntimeStatus(RuntimeId.OpenAiCompat("lmstudio"), true, null, new Uri("http://127.0.0.1:1234/v1")),
                ],
                [new DeviceModel(KnownModels.Qwen35, RuntimeId.Ollama, InstallState.Ready, Fit.Good)],
                [new Install(KnownModels.Gemma4.Id, RuntimeId.Ollama, InstallState.Downloading, 42, 420, 1000, null, null)]));

        try
        {
            await using var os = TestOS.Local(o => o.Services = services)
                .Register(typeof(AppsModule).Assembly)
                .Register<FakeSettingsDevice>();
            var settings = await os.Install<SettingsApp>();
            await settings.Request("navigate", new SettingsNavigate("models"));

            var rendered = System.Text.Json.JsonSerializer.Serialize(settings.App.Render());
            Assert.Contains("32 GB RAM", rendered, StringComparison.Ordinal);
            Assert.Contains("12 GB VRAM", rendered, StringComparison.Ordinal);
            Assert.Contains("CUDA", rendered, StringComparison.Ordinal);
            Assert.Contains("lmstudio", rendered, StringComparison.Ordinal);
            Assert.Contains("Downloading 42%", rendered, StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(tempDir)) { Directory.Delete(tempDir, recursive: true); }
        }
    }

    [Fact]
    public void WebToolsPageShowsProviderKeysAndServers()
    {
        var state = new SettingsAppState().Navigate("models", "web-tools") with
        {
            WebSearchProvider = "Searxng",
            WebSearxngUrl = "http://localhost:8080",
            McpServers =
            [
                new McpServerRow(
                    Name: "microsoft-learn",
                    Title: "Microsoft Learn",
                    Url: "https://learn.microsoft.com/api/mcp",
                    Connected: true,
                    ToolCount: 3),
            ],
        };

        var node = AggregateExplorer<SettingsAppState>.PreviewNode<SettingsApp>(state);
        var json = System.Text.Json.JsonSerializer.Serialize(
            node,
            DigitalBrain.Invocation.RuntimeProtocol.OptionsFor(typeof(Node)));

        Assert.Contains("web-search-provider", json, StringComparison.Ordinal);
        Assert.Contains("web-searxng-url", json, StringComparison.Ordinal);
        Assert.Contains("\"key\":\"models:open-keys\"", json, StringComparison.Ordinal);
        Assert.Contains("\"key\":\"mcp:microsoft-learn\"", json, StringComparison.Ordinal);
        Assert.Contains("Microsoft Learn", json, StringComparison.Ordinal);
        Assert.Contains("https://learn.microsoft.com/api/mcp", json, StringComparison.Ordinal);
        Assert.Contains("Brave", json, StringComparison.Ordinal);
        Assert.Contains("Tavily", json, StringComparison.Ordinal);
        Assert.Contains("Chat model", json, StringComparison.Ordinal);

        var filtered = SettingsApp.FilterSections("searxng");
        Assert.Contains(filtered, entry => entry.SectionId == "models");
    }

    [Fact]
    public async Task ChoosingBraveWritesTheBrainVariable()
    {
        var brain = new BrainId("local");
        await using var os = TestOS.Local().Register(typeof(AppsModule).Assembly);
        var settings = await os.Install<SettingsApp>();
        await settings.Request("navigate", new SettingsNavigate("models", SubPage: "web-tools"));

        using (os.Edge.Enter(os.Edge.Person("local", "local", brain)))
        {
            var select = new DigitalBrain.UI.Commands.Select("Brave");
            await settings.Screen.Dispatch(new DigitalBrain.UI.Command(
                SettingsControls.WebSearchProvider,
                "control.select",
                System.Text.Json.JsonSerializer.SerializeToElement(select, System.Text.Json.JsonSerializerOptions.Web)));
        }

        await os.Idle();

        Assert.Equal("models", settings.App.State.Section);
        Assert.Equal("web-tools", settings.App.State.SubPage);
        Assert.Equal("Brave", settings.App.State.WebSearchProvider);

        using (os.Edge.Enter(os.Edge.Person("local", "local", brain)))
        {
            var stored = await os.Get<IUserSettings>(new NeuronId(brain, "os.settings", "settings")).Read();
            Assert.Contains(stored.Variables ?? [], v => v.Key == "web.search.provider" && v.Value == "Brave");
        }
    }

    [Fact]
    public async Task SettingsInstallModelFormCallsDeviceInstall()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"intochat-settings-device-{Guid.NewGuid():N}");
        var machine = new MachineId(Path.Combine(tempDir, "machine-id"));
        var services = new ServiceCollection().AddSingleton(machine).BuildServiceProvider();

        FakeSettingsDevice.Reset(
            machine.Value,
            new DeviceView(
                machine.Value,
                new Hardware(32L * 1024 * 1024 * 1024, 12L * 1024 * 1024 * 1024, [Accelerator.Cpu, Accelerator.Cuda]),
                [new RuntimeStatus(RuntimeId.Ollama, true, "0.9.0", new Uri("http://127.0.0.1:11434"))],
                [],
                []));

        try
        {
            await using var os = TestOS.Local(o => o.Services = services)
                .Register(typeof(AppsModule).Assembly)
                .Register<FakeSettingsDevice>();
            var settings = await os.Install<SettingsApp>();
            await settings.Request("navigate", new SettingsNavigate("models"));

            using (os.Edge.Enter(os.Edge.Person("local", "local", new BrainId("local"))))
            {
                await settings.Screen.Dispatch(new Command(
                    SettingsControls.InstallModel,
                    "control.submit",
                    System.Text.Json.JsonSerializer.SerializeToElement(
                        new Submit(new Dictionary<string, string> { ["model"] = KnownModels.Gemma4.Id.Value }),
                        typeof(Submit),
                        System.Text.Json.JsonSerializerOptions.Web)));
            }
            await os.Idle();

            Assert.Equal(KnownModels.Gemma4.Id, Assert.Single(FakeSettingsDevice.Installs));
        }
        finally
        {
            if (Directory.Exists(tempDir)) { Directory.Delete(tempDir, recursive: true); }
        }
    }

    [DigitalBrain.Neurons.Neuron("devices.device")]
    private sealed class FakeSettingsDevice : DigitalBrain.Neurons.Neuron, IDevice
    {
        private static DeviceView _view = new("m1", new Hardware(0, 0, [Accelerator.Cpu]), [], [], []);
        public static List<ModelId> Installs { get; } = [];

        public static void Reset(string machineId, DeviceView view)
        {
            _view = view with { MachineId = machineId };
            Installs.Clear();
        }

        public Task<DeviceView> Read() => Task.FromResult(_view);
        public Task Use(Role role, ModelId model) => Task.CompletedTask;
        public Task Install(ModelId model)
        {
            Installs.Add(model);
            return Task.CompletedTask;
        }
        public Task Cancel(ModelId model) => Task.CompletedTask;
        public Task Remove(ModelId model) => Task.CompletedTask;
    }

    [Fact]
    public async Task AddingAServerConnectsIt()
    {
        var brain = new BrainId("local");
        RecordingMcpServerNeuron.Reset();
        try
        {
            await using var os = TestOS.Local()
                .Register(typeof(AppsModule).Assembly)
                .Register<RecordingMcpServerNeuron>();
            var settings = await os.Install<SettingsApp>();
            await settings.Request("navigate", new SettingsNavigate("models", SubPage: "web-tools"));

            await settings.Request("mcp-add", new SettingsMcpAdd("docs", "https://example.org/mcp"));
            await os.Idle();

            Assert.Equal(
                new IntoChat.OS.Modules.Mcp.McpConnectionConfig("https://example.org/mcp", "docs"),
                RecordingMcpServerNeuron.LastConnectedConfig);
            Assert.Equal("connection/mcp/docs", RecordingMcpServerNeuron.LastConnectedKey);
            Assert.Contains(settings.App.State.McpServers, row =>
                row.Name == "docs"
                && row.Url == "https://example.org/mcp"
                && row.Connected
                && row.ToolCount == 1);

            await settings.Request("mcp-remove", new SettingsMcpRemove("docs"));
            await os.Idle();

            Assert.Equal("connection/mcp/docs", RecordingMcpServerNeuron.LastDisconnectedKey);
            Assert.Contains(settings.App.State.McpServers, row => row.Name == "docs" && !row.Connected);
        }
        finally
        {
            RecordingMcpServerNeuron.Reset();
        }
    }

    [Fact]
    public async Task KeysPageListsBraveAndTavily()
    {
        await using var os = TestOS.Local().Register(typeof(AppsModule).Assembly);
        var settings = await os.Install<SettingsApp>();
        var vault = new MemorySecretVault();
        settings.App.SecretVault = vault;

        await settings.App.Navigate(new SettingsNavigate("keys"));
        var json = System.Text.Json.JsonSerializer.Serialize(
            settings.App.Render(),
            new System.Text.Json.JsonSerializerOptions
            {
                Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            });

        Assert.Contains("Brave Search", json, StringComparison.Ordinal);
        Assert.Contains("BRAVE_API_KEY", json, StringComparison.Ordinal);
        Assert.Contains("Tavily", json, StringComparison.Ordinal);
        Assert.Contains("TAVILY_API_KEY", json, StringComparison.Ordinal);

        vault.SetSecret("BRAVE_API_KEY");
        await settings.Request("open", new SettingsOpen("keys"));
        await settings.App.Navigate(new SettingsNavigate("keys"));
        var updatedJson = System.Text.Json.JsonSerializer.Serialize(settings.App.Render());
        Assert.Contains("\"Status\":\"saved\"", updatedJson, StringComparison.Ordinal);
    }

    [Fact]
    public void OldSettingsStateWithoutTheNewMembersLoads()
    {
        const string legacyJson = """
            {
              "title": "Settings",
              "status": "Settings",
              "displayName": "Ada",
              "theme": "dark",
              "defaultModel": "qwen3.5:4b",
              "section": "models"
            }
            """;

        var loaded = System.Text.Json.JsonSerializer.Deserialize<SettingsAppState>(
            legacyJson,
            System.Text.Json.JsonSerializerOptions.Web);

        Assert.NotNull(loaded);
        Assert.Equal("Ada", loaded.DisplayName);
        Assert.Empty(loaded.McpServers);
        Assert.Null(loaded.WebSearchProvider);
    }

    [Fact]
    public async Task SearxngUrlFormAndMcpAddFormAndObserverWorkEndToEnd()
    {
        var brain = new BrainId("local");
        RecordingMcpServerNeuron.Reset();
        try
        {
            await using var os = TestOS.Local()
                .Register(typeof(AppsModule).Assembly)
                .Register<RecordingMcpServerNeuron>();
            var settings = await os.Install<SettingsApp>();
            settings.App.Observer = os;
            settings.App.WebOptions = Microsoft.Extensions.Options.Options.Create(new IntoChat.OS.Modules.Web.WebModuleOptions
            {
                Provider = IntoChat.OS.Modules.Web.SearchProvider.Searxng,
                SearxngUrl = new Uri("http://localhost:8080"),
            });

            await settings.Request("navigate", new SettingsNavigate("models", SubPage: "web-tools"));
            Assert.Equal("Searxng", settings.App.State.WebSearchProvider);
            Assert.Equal("http://localhost:8080/", settings.App.State.WebSearxngUrl);

            using (os.Edge.Enter(os.Edge.Person("local", "local", brain)))
            {
                var submitUrl = new DigitalBrain.UI.Commands.Submit(new Dictionary<string, string>
                {
                    ["url"] = "http://localhost:8888",
                });
                await settings.Screen.Dispatch(new DigitalBrain.UI.Command(
                    SettingsControls.SearxngUrl,
                    "control.submit",
                    System.Text.Json.JsonSerializer.SerializeToElement(submitUrl, System.Text.Json.JsonSerializerOptions.Web)));

                var submitMcp = new DigitalBrain.UI.Commands.Submit(new Dictionary<string, string>
                {
                    ["name"] = "learn",
                    ["url"] = "https://learn.microsoft.com/api/mcp",
                });
                await settings.Screen.Dispatch(new DigitalBrain.UI.Command(
                    SettingsControls.McpAddForm,
                    "control.submit",
                    System.Text.Json.JsonSerializer.SerializeToElement(submitMcp, System.Text.Json.JsonSerializerOptions.Web)));
            }

            await os.Idle();

            using (os.Edge.Enter(os.Edge.Person("local", "local", brain)))
            {
                var stored = await os.Get<IUserSettings>(new NeuronId(brain, "os.settings", "settings")).Read();
                Assert.Contains(stored.Variables ?? [], v => v.Key == "web.search.searxng-url" && v.Value == "http://localhost:8888");
            }

            Assert.Equal(
                new IntoChat.OS.Modules.Mcp.McpConnectionConfig("https://learn.microsoft.com/api/mcp", "learn"),
                RecordingMcpServerNeuron.LastConnectedConfig);

            // Re-navigating refreshes servers from IBrainObserver + IMcpToolConnection.ReadState()
            await settings.Request("navigate", new SettingsNavigate("models", SubPage: "web-tools"));
            Assert.Contains(settings.App.State.McpServers, r => r.Name == "learn" && r.Connected && r.ToolCount == 1);
        }
        finally
        {
            RecordingMcpServerNeuron.Reset();
        }
    }
}

[DigitalBrain.Neurons.Neuron("mcp"), DigitalBrain.Neurons.StatelessNeuron]
internal sealed class RecordingMcpServerNeuron : DigitalBrain.Neurons.Neuron, IntoChat.OS.Modules.Mcp.IMcpToolConnection
{
    public static IntoChat.OS.Modules.Mcp.McpConnectionConfig? LastConnectedConfig { get; private set; }
    public static string? LastConnectedKey { get; private set; }
    public static string? LastDisconnectedKey { get; private set; }

    public static void Reset()
    {
        LastConnectedConfig = null;
        LastConnectedKey = null;
        LastDisconnectedKey = null;
    }

    public Task<IntoChat.OS.Modules.Mcp.McpServerState> ConnectServer(IntoChat.OS.Modules.Mcp.McpConnectionConfig config, CancellationToken ct = default)
    {
        LastConnectedConfig = config;
        LastConnectedKey = Id.Key;
        return Task.FromResult(new IntoChat.OS.Modules.Mcp.McpServerState(
            config.Title ?? "",
            config.Url,
            Connected: true,
            Tools: [new IntoChat.OS.Modules.Tools.ToolDescriptor("search", "Search", "Search docs", "{}", true)],
            Error: null,
            RefreshedAt: DateTimeOffset.UtcNow));
    }

    public Task<IntoChat.OS.Modules.Mcp.McpServerState> Configure(IntoChat.OS.Modules.Mcp.McpConnectionConfig config, CancellationToken ct = default)
        => ConnectServer(config, ct);

    public Task DisconnectServer(CancellationToken ct = default)
    {
        LastDisconnectedKey = Id.Key;
        return Task.CompletedTask;
    }

    public Task<IntoChat.OS.Modules.Mcp.McpServerState> ReadState(CancellationToken ct = default)
        => Task.FromResult(LastConnectedConfig is { } config && LastDisconnectedKey != Id.Key
            ? new IntoChat.OS.Modules.Mcp.McpServerState(
                config.Title ?? "",
                config.Url,
                Connected: true,
                Tools: [new IntoChat.OS.Modules.Tools.ToolDescriptor("search", "Search", "Search docs", "{}", true)],
                Error: null,
                RefreshedAt: DateTimeOffset.UtcNow)
            : new IntoChat.OS.Modules.Mcp.McpServerState("", "", false, [], null, null));

    public Task<IReadOnlyList<string>> List()
        => Task.FromResult<IReadOnlyList<string>>(["search"]);

    public Task<System.Text.Json.JsonElement> Run(string tool, System.Text.Json.JsonElement args, CancellationToken ct = default)
        => Task.FromResult(System.Text.Json.JsonDocument.Parse("{}").RootElement.Clone());

    public Task<IReadOnlyList<IntoChat.OS.Modules.Tools.ToolDescriptor>> Search(string query, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<IntoChat.OS.Modules.Tools.ToolDescriptor>>([]);

    public Task<bool> Validate(string tool, System.Text.Json.JsonElement args, CancellationToken ct = default)
        => Task.FromResult(true);

    public Task<IntoChat.OS.Modules.Tools.ToolSet> Tools(CancellationToken ct = default)
        => Task.FromResult(new IntoChat.OS.Modules.Tools.ToolSet("", []));

    public Task<IntoChat.OS.Modules.Tools.ToolResult> Run(string tool, string arguments, CancellationToken ct = default)
        => Task.FromResult(new IntoChat.OS.Modules.Tools.ToolResult(false, "", []));
}
