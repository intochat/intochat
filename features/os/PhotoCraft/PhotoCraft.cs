using System.Globalization;
using DigitalBrain.UI;
using IntoChat.OS.Modules.Apps;
using IntoChat.OS.Modules.AI.Agents;
using IntoChat.OS.Modules.ArtCraft;
using IntoChat.OS.Client;
using IntoChat.OS.Modules.Flutter;
using IntoChat.OS.Modules.Flutter.Views;
using IntoChat.OS.Modules.Mcp;
using UI = DigitalBrain.UI.Ui;
using ViewUi = IntoChat.OS.Modules.Flutter.Views.Ui;

public sealed record PhotoCraftScriptState(
    string Status = "PhotoCraft tools connected.",
    string WebUrl = "http://localhost:18082",
    string ExecutionMode = "wasm",
    string? PreviewDataUrl = null)
{
    public PhotoCraftScriptState() : this("PhotoCraft tools connected.", "http://localhost:18082", "wasm", null)
    {
    }
}

[DigitalBrain.App("photocraft", Icon = "image", Accent = "#F59E0B", Category = "Creative")]
public sealed class PhotoCraftScriptApp : DigitalBrain.App<PhotoCraftScriptState>
{
    [DigitalBrain.On("open")]
    public Task Open()
    {
        Set(State with { Status = "PhotoCraft tools connected." });
        return Task.CompletedTask;
    }

    [DigitalBrain.On("edit")]
    public Task Edit(string? request)
    {
        var text = (request ?? "").Trim();
        Set(State with { Status = text.Length == 0 ? "Nothing to edit." : "Editing: " + text });
        return Task.CompletedTask;
    }

    [DigitalBrain.On("preview")]
    public Task Preview()
    {
        Set(State);
        return Task.CompletedTask;
    }

    public View Render() => new(
        "PhotoCraft",
        new Column(
        [
            new Surface(
                [
                    new Text(State.Status),
                ],
                Kind: SurfaceKind.Elevated,
                Title: "PhotoCraft Layered Studio",
                Subtitle: $"Mode: {State.ExecutionMode} · {State.WebUrl}"),
            UI.WebBrowser(
                State.WebUrl,
                "PhotoCraft WASM Studio",
                "photocraft-wasm",
                520,
                State.ExecutionMode),
        ]));
}

// Shared by every PhotoCraft synapse: the app's declarative view surface, connecting the tools, and composing main content.
public static class PhotoCraft
{
    public const int MaxRequestChars = 4000;
    public const int MaxStatusChars = 4000;

    public static string Key(IntoChatOSConnection brain) => brain.Setting("App")!;
    public static string Name(IntoChatOSConnection brain, string part) => Key(brain) + "/" + part;

    public static IAgent Editor(IntoChatOSConnection brain) => brain.Get<IAgent>(Name(brain, "editor"));
    public static IView Surface(IntoChatOSConnection brain) => brain.Get<IView>(Name(brain, "view"));

    public static string Clip(string text, int max) => text.Length <= max ? text : text[..max];

    public static Task<ArtCraftSession> Connect(IntoChatOSConnection brain, CancellationToken ct)
        => brain.Get<IArtCraftStudio>(brain.Setting("Brain")! + "/artcraft").Open("photocraft", ct);

    public static async Task SetStatus(IntoChatOSConnection brain, string status, string? webUrl = null, string? settledRunId = null)
    {
        var current = await Surface(brain).Read();
        var url = webUrl ?? current?.Root.Children?.ElementAtOrDefault(1)?.Text ?? "http://localhost:18082";
        var settled = settledRunId ?? current?.Root.Children?.ElementAtOrDefault(2)?.Text ?? "";
        var preview = current?.Root.Children?.ElementAtOrDefault(3)?.Text ?? "";
        await PublishView(brain, status, url, settled, preview);
    }

    public static async Task Show(IntoChatOSConnection brain, ArtCraftSession session, CancellationToken ct)
    {
        _ = ct;
        var current = await Surface(brain).Read();
        var status = current?.Root.Children?.FirstOrDefault()?.Text ?? "PhotoCraft tools connected.";
        var settled = current?.Root.Children?.ElementAtOrDefault(2)?.Text ?? "";
        var preview = current?.Root.Children?.ElementAtOrDefault(3)?.Text ?? "";
        await PublishView(brain, status, session.WebUrl ?? "http://localhost:18082", settled, preview);
    }

    public static async Task Compose(IntoChatOSConnection brain)
    {
        var current = await Surface(brain).Read();
        var status = current?.Root.Children?.FirstOrDefault()?.Text ?? "PhotoCraft is not connected.";
        var url = current?.Root.Children?.ElementAtOrDefault(1)?.Text ?? "http://localhost:18082";
        var settled = current?.Root.Children?.ElementAtOrDefault(2)?.Text ?? "";
        var preview = current?.Root.Children?.ElementAtOrDefault(3)?.Text ?? "";
        await PublishView(brain, status, url, settled, preview);
    }

    private static async Task PublishView(IntoChatOSConnection brain, string status, string webUrl, string settledRunId, string previewDataUrl)
    {
        var revision = long.Parse(brain.Setting("ProgramRevision") ?? "1", CultureInfo.InvariantCulture);
        var publisher = new ViewPublisher(Surface(brain), revision);
        var resolvedUrl = string.IsNullOrWhiteSpace(webUrl) ? "http://localhost:18082" : webUrl;
        await publisher.Publish(ViewUi.Column(
            "photocraft",
            [
                ViewUi.Text("status", status),
                ViewUi.Card(
                    "browser",
                    resolvedUrl,
                    detail: $"{{\"_ui\":\"webbrowser\",\"url\":\"{resolvedUrl}\",\"title\":\"PhotoCraft WASM Studio\",\"height\":0,\"executionMode\":\"wasm-ui\"}}"),
                ViewUi.Text("settled", settledRunId),
                ViewUi.Text("preview", previewDataUrl),
            ],
            extents: [24, 0, 2, 2]));
        await brain.Get<IApp>(Key(brain)).Present(
            revision,
            new(
                new Sphere("icon.svg", []),
                Main: new(UIVocabulary.ViewType, Key(brain) + "/view"),
                Views: [new("full", new(UIVocabulary.ViewType, Key(brain) + "/view"))]));
    }

    public static async Task<AgentRun?> Unsettled(IntoChatOSConnection brain)
    {
        var state = await Editor(brain).Read();
        if (state.ActiveRun is not null || state.Runs.LastOrDefault() is not { } last) { return null; }
        var current = await Surface(brain).Read();
        var settled = current?.Root.Children?.ElementAtOrDefault(2)?.Text ?? "";
        return settled == last.RunId ? null : last;
    }

    public static IMcpServer Tools(IntoChatOSConnection brain) => brain.Get<IMcpServer>(brain.Setting("Brain")! + "/mcp/photocraft");

    public const string ChangedWithTools = "Changed with PhotoCraft's tools.";

    public static readonly string[] Reads = ["render_preview", "doc_render_preview", "doc_inspect", "command_list",
        "session_list", "jobs_list", "ui_inspect", "ui_screenshot", "doc_save", "doc_export"];

    public static async Task<string?> Render(IMcpServer tools, CancellationToken ct)
    {
        var shot = await tools.Run("render_preview", "{\"max_side\":512}", ct);
        return !shot.IsError && shot.Blobs.FirstOrDefault(blob => blob.MediaType == "image/png" && blob.Data.Length > 0) is { } png
            ? "data:image/png;base64," + png.Data : null;
    }

    public static async Task RenderPreview(IntoChatOSConnection brain, ArtCraftSession session, string request, CancellationToken ct)
    {
        _ = request;
        if (await Render(brain.Get<IMcpServer>(session.McpKey), ct) is { } png)
        {
            var current = await Surface(brain).Read();
            var status = current?.Root.Children?.FirstOrDefault()?.Text ?? "Done.";
            var url = session.WebUrl ?? current?.Root.Children?.ElementAtOrDefault(1)?.Text ?? "http://localhost:18082";
            var settled = current?.Root.Children?.ElementAtOrDefault(2)?.Text ?? "";
            await PublishView(brain, status, url, settled, png);
        }
    }
}
