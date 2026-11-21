using DigitalBrain.UI;
using static IntoChat.OS.UI.Kit;

namespace IntoChat.OS.UI;

/// <summary>
/// Shared DDD WebSurface base class for full-bleed embedded browser apps
/// (Orleans, Aspire, Widgetbook, DevUI, ArtCraft).
/// Eliminates duplicated AppShell + frameless WebBrowser boilerplate across apps.
/// </summary>
public abstract class EmbeddedWebApp<TState> : AggregateScreen<TState>
    where TState : class, new()
{
    protected static Node RenderEmbeddedSurface(
        string shellKey,
        string browserKey,
        string title,
        string url,
        Node? toolbar = null,
        string transition = "",
        string executionMode = "wasm-ui")
    {
        var browser = WebBrowser(
            url,
            title,
            key: browserKey,
            height: 920,
            executionMode: executionMode,
            automationName: title,
            chrome: "frameless");

        return AppShell(
            shellKey,
            sidebar: null,
            body: browser,
            toolbar: toolbar,
            inspector: null,
            automationName: title,
            transition: transition,
            edgeToEdge: true);
    }
}
