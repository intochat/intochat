using DigitalBrain.Testing;

namespace IntoChat.OS.Modules.Apps.Tests.Unit;

public sealed class SettingsNavigationFacts
{
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
        foreach (var section in sections)
        {
            app.Navigate(new SettingsNavigate(section));
            Assert.Equal(section, app.State.Section);
            var view = app.Render();
            var mapped = AppNeuron.MapView(view, "owner");
            var json = System.Text.Json.JsonSerializer.Serialize(mapped, jsonOptions);
            Assert.Contains(SettingsApp.SearchIndex.Single(s => s.SectionId == section).Title, json, StringComparison.Ordinal);
        }

        // Subpages: AppPage, MemoryPage, ConnectionPage, PersonPage, LocalServerPage
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

        // Modal sheet & search filter
        app.Navigate(new SettingsNavigate("connections", SubPage: "google_drive", Sheet: "google_drive", SearchQuery: "Ollama"));
        Assert.Equal("google_drive", app.State.Sheet);
        var sheetJson = System.Text.Json.JsonSerializer.Serialize(AppNeuron.MapView(app.Render(), "owner"), jsonOptions);
        Assert.Contains("Sheet · google_drive", sheetJson, StringComparison.Ordinal);

        var filtered = SettingsApp.FilterSections("Ollama");
        Assert.Single(filtered);
        Assert.Equal("models", filtered[0].SectionId);
    }

    [Fact]
    public async Task InoAutonomouslyRoutesSettingsNavigationAndGatesDestructiveSettingsActions()
    {
        await using var os = TestOS.Local();
        os.Register(typeof(AppsModule).Assembly);

        var ino = await os.Install<InoApp>();
        await ino.Request("send", new InoSend("Where do I set up my local Ollama server?"));
        Assert.Equal("routed:settings.navigate:models:local-server", ino.App.State.Turns[^1].RouteHint);
        Assert.Contains(ino.App.State.ActiveCards, c => c.Kind == "settings" && c.SourceNeuronUri == "neuron://local/apps/settings");

        await ino.Request("send", new InoSend("Open Google Drive connection sheet in settings"));
        Assert.Equal("routed:settings.navigate:connections:google_drive:google_drive", ino.App.State.Turns[^1].RouteHint);

        await ino.Request("send", new InoSend("Revoke API key in settings"));
        Assert.Equal("routed:settings.navigate:keys", ino.App.State.Turns[^1].RouteHint);
        var approval = Assert.Single(ino.App.State.ActiveCards, c => c.Kind == "approval" && c.SourceNeuronUri == "neuron://local/os.settings/settings");
        Assert.Equal("pending", approval.Status);
    }
}
