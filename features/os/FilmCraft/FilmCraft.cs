using System.Globalization;
using DigitalBrain.UI;
using IntoChat.OS.Client;
using IntoChat.OS.Modules.Apps;
using IntoChat.OS.Modules.Flutter;
using IntoChat.OS.Modules.Flutter.Views;
using UI = DigitalBrain.UI.Ui;
using ViewUi = IntoChat.OS.Modules.Flutter.Views.Ui;

public sealed record FilmCraftScriptState(
    string Status = "FilmCraft tools connected.",
    string WebUrl = "http://localhost:18083",
    string ExecutionMode = "wasm")
{
    public FilmCraftScriptState() : this("FilmCraft tools connected.", "http://localhost:18083", "wasm")
    {
    }
}

[DigitalBrain.App("filmcraft", Icon = "film", Accent = "#A855F7", Category = "Creative")]
public sealed class FilmCraftScriptApp : DigitalBrain.App<FilmCraftScriptState>
{
    [DigitalBrain.On("open")]
    public Task Open()
    {
        Set(State with { Status = "FilmCraft tools connected." });
        return Task.CompletedTask;
    }

    public View Render() => new(
        "FilmCraft",
        new Column(
        [
            new Surface(
                [
                    new Text(State.Status),
                ],
                Kind: SurfaceKind.Elevated,
                Title: "FilmCraft NLE Studio",
                Subtitle: $"Mode: {State.ExecutionMode} · {State.WebUrl}"),
            UI.WebBrowser(
                State.WebUrl,
                "FilmCraft WASM Studio",
                "filmcraft-wasm",
                520,
                State.ExecutionMode),
        ]));
}

public static class FilmCraftView
{
    public static async Task Publish(IntoChatOSConnection brain, string status, string? webUrl)
    {
        var appKey = brain.Setting("App")!;
        var revision = long.Parse(brain.Setting("ProgramRevision") ?? "1", CultureInfo.InvariantCulture);
        var publisher = new ViewPublisher(brain.Get<IView>(appKey + "/view"), revision);
        var resolvedUrl = string.IsNullOrWhiteSpace(webUrl) ? "http://localhost:18083" : webUrl;
        await publisher.Publish(ViewUi.Column(
            "filmcraft",
            [
                ViewUi.Text("status", status),
                ViewUi.Card(
                    "browser",
                    resolvedUrl,
                    detail: $"{{\"_ui\":\"webbrowser\",\"url\":\"{resolvedUrl}\",\"title\":\"FilmCraft WASM Studio\",\"height\":0,\"executionMode\":\"wasm-ui\"}}"),
            ],
            extents: [24, 0]));
        await brain.Get<IApp>(appKey).Present(
            revision,
            new(
                new Sphere("icon.svg", []),
                Main: new(UIVocabulary.ViewType, appKey + "/view"),
                Views: [new("full", new(UIVocabulary.ViewType, appKey + "/view"))]));
    }
}
