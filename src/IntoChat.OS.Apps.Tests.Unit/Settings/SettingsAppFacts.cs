using DigitalBrain;
using DigitalBrain.Testing;
using DigitalBrain.UI;
using IntoChat.OS.Apps.Settings;
using IntoChat.OS.Modules.Apps;

namespace IntoChat.OS.Apps.Tests.Unit.Settings;

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
    }
}
