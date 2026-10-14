using DigitalBrain.Apps;
using DigitalBrain.AI.Agents;
using DigitalBrain.ArtCraft;
using DigitalBrain.Client;
using DigitalBrain.Flutter;
using DigitalBrain.Flutter.Image;
using DigitalBrain.Flutter.Layout;
using DigitalBrain.Flutter.Text;
using DigitalBrain.Flutter.WebBrowser;
using DigitalBrain.Mcp;

// Shared by every PhotoCraft synapse: the app's neurons, connecting the tools, and composing main content.
// Every call here is a plain grain call that is safe to repeat.
public static class PhotoCraft
{
    public const int MaxRequestChars = 4000;
    public const int MaxStatusChars = 4000;

    public static string Key(DigitalBrainConnection brain) => brain.Setting("App")!;
    public static string Name(DigitalBrainConnection brain, string part) => Key(brain) + "/" + part;

    public static IText Status(DigitalBrainConnection brain) => brain.Get<IText>(Name(brain, "status"));
    public static IAgent Editor(DigitalBrainConnection brain) => brain.Get<IAgent>(Name(brain, "editor"));
    public static IImage Preview(DigitalBrainConnection brain) => brain.Get<IImage>(Name(brain, "preview"));

    // The run id of the editor turn the app last settled, so a replayed signal settles it once.
    public static IText Settled(DigitalBrainConnection brain) => brain.Get<IText>(Name(brain, "settled"));

    public static string Clip(string text, int max) => text.Length <= max ? text : text[..max];

    // Connects PhotoCraft's MCP server to the brain; cheap when it is already connected.
    public static Task<ArtCraftSession> Connect(DigitalBrainConnection brain, CancellationToken ct)
        => brain.Get<IArtCraftStudio>(brain.Setting("Brain")! + "/artcraft").Open("photocraft", ct);

    // The main content: the status line above the PhotoCraft web app.
    public static async Task Show(DigitalBrainConnection brain, ArtCraftSession session, CancellationToken ct)
    {
        var browser = brain.Get<IWebBrowser>(Name(brain, "browser"));
        await browser.Configure(Name(brain, "browser"));
        if (session.WebUrl is { } url && (await browser.Read()).Uri != url) { await browser.Navigate(url, "PhotoCraft"); }
        await Compose(brain);
    }

    public static async Task Compose(DigitalBrainConnection brain)
    {
        UiChildRef Ref(string kind, string part) => new(kind, Name(brain, part));
        var main = brain.Get<ILayout>(Name(brain, "main"));
        await main.Set(new LayoutDefinition("column", [Ref(UIVocabulary.TextType, "status"), Ref(UIVocabulary.WebBrowserType, "browser")],
            Extents: [40, 0]), (await main.Read()).Revision);
        await brain.Get<IApp>(Key(brain)).Present(long.Parse(brain.Setting("ProgramRevision")!, System.Globalization.CultureInfo.InvariantCulture),
            new(new Sphere("icon.svg", []), Main: new("layout", Key(brain) + "/main"), Views: [new("full", new("layout", Key(brain) + "/main"))]));
    }

    // The editor's last turn when it has ended and the app has not settled it yet.
    public static async Task<AgentRun?> Unsettled(DigitalBrainConnection brain)
    {
        var state = await Editor(brain).Read();
        if (state.ActiveRun is not null || state.Runs.LastOrDefault() is not { } last) { return null; }
        return (await Settled(brain).Read()).Markdown == last.RunId ? null : last;
    }

    // The brain's PhotoCraft MCP server, at the key IArtCraftStudio.Open answers as McpKey. Only a
    // reference: it connects nothing.
    public static IMcpServer Tools(DigitalBrainConnection brain) => brain.Get<IMcpServer>(brain.Setting("Brain")! + "/mcp/photocraft");

    // The caption of a preview rendered after PhotoCraft's tools changed the document outside an edit. It
    // names no tool: signals arrive late, so the render may already show what several tools did.
    public const string ChangedWithTools = "Changed with PhotoCraft's tools.";

    // Tools that only read or render the document: running one changes nothing the preview shows.
    public static readonly string[] Reads = ["render_preview", "doc_render_preview", "doc_inspect", "command_list",
        "session_list", "jobs_list", "ui_inspect", "ui_screenshot", "doc_save", "doc_export"];

    // The active document as a PNG data URL, or null when nothing could be rendered.
    public static async Task<string?> Render(IMcpServer tools, CancellationToken ct)
    {
        var shot = await tools.Run("render_preview", "{\"max_side\":512}", ct);
        return !shot.IsError && shot.Blobs.FirstOrDefault(blob => blob.MediaType == "image/png" && blob.Data.Length > 0) is { } png
            ? "data:image/png;base64," + png.Data : null;
    }

    // Renders the active document into the preview image; the request is the image's caption.
    public static async Task RenderPreview(DigitalBrainConnection brain, ArtCraftSession session, string request, CancellationToken ct)
    {
        if (await Render(brain.Get<IMcpServer>(session.McpKey), ct) is { } png) { await Preview(brain).Set(png, "image/png", Clip(request, 500)); }
    }
}
