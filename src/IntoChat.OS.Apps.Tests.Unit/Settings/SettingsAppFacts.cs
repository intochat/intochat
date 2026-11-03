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
        Assert.Contains("No modules are loaded.", bundle.Files["SettingsView.cs"], StringComparison.Ordinal);
        Assert.Contains("/apps-view", bundle.Files["Apps.cs"], StringComparison.Ordinal);
        Assert.Contains("[DigitalBrain.App(\"settings\")]", bundle.Files["SettingsProgram.cs"], StringComparison.Ordinal);
        Assert.Contains("\"apps-view\"", bundle.Files["SettingsProgram.cs"], StringComparison.Ordinal);
        Assert.DoesNotContain(bundle.Files.Values, source => source.Contains("Apps.Present(", StringComparison.Ordinal) && !source.Contains("public static Task Present", StringComparison.Ordinal));
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
            var mapped = AppNeuron.MapView(view, "owner");
            var json = System.Text.Json.JsonSerializer.Serialize(mapped, jsonOptions);
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
            var json = System.Text.Json.JsonSerializer.Serialize(AppNeuron.MapView(app.Render(), "owner"), jsonOptions);
            Assert.Contains(marker, json, StringComparison.Ordinal);
        }

        app.Navigate(new SettingsNavigate("connections", SubPage: "google_drive", Sheet: "google_drive", SearchQuery: "Ollama"));
        Assert.Equal("google_drive", app.State.Sheet);
        var sheetJson = System.Text.Json.JsonSerializer.Serialize(AppNeuron.MapView(app.Render(), "owner"), jsonOptions);
        Assert.Contains("Sheet · google_drive", sheetJson, StringComparison.Ordinal);

        var filtered = SettingsApp.FilterSections("Ollama");
        Assert.Single(filtered);
        Assert.Equal("models", filtered[0].SectionId);
    }

    [Fact]
    public async Task SettingsPresentsTheSideloadListInstallAndCloneThroughTheNeuron()
    {
        await using var os = TestOS.Local();
        os.Register<FakeSideloadNeuron>();
        var installed = await os.Install<SettingsApp>();

        await installed.Request("sideload-list", new SettingsSideloadList());
        var listed = Assert.IsType<Collection>(installed.App.State.OverrideBody);
        var entry = Assert.Single(listed.Entries);
        Assert.Equal("notes", entry.Id);
        Assert.Equal("not installed", entry.Subtitle);

        await installed.Request("sideload-install", new SettingsSideloadInstall("notes"));
        Assert.Equal("Installed notes.", Assert.IsType<Text>(installed.App.State.OverrideBody).Value);

        await installed.Request("sideload-add-git", new SettingsSideloadAddGit("https://example.com/notes.git"));
        Assert.Equal("Cloned notes.", Assert.IsType<Text>(installed.App.State.OverrideBody).Value);
    }

    [Neuron("apps.sideload")]
    private sealed class FakeSideloadNeuron : Neuron, ISideload
    {
        public Task<SideloadRow[]> List()
            => Task.FromResult<SideloadRow[]>([new SideloadRow("notes", "Notes", false)]);

        public Task<SideloadResult> Install(string? name, string? consent = null)
            => Task.FromResult(new SideloadResult(true, $"Installed {name}."));

        public Task<SideloadResult> AddGit(string? url)
            => Task.FromResult(new SideloadResult(true, "Cloned notes."));
    }
}
