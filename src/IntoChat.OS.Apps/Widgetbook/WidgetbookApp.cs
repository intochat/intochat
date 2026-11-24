/*
# Widgetbook UI Kit Workbench
Role: Operator
Shipped: true
Operations: open, switch-source, set-theme, reload, ui.command

Built-in full-bleed WebBrowser UI Kit workbench (`app.widgetbook`) hosting IntoChat OS's
open-source Flutter Widgetbook catalog at `/widgetbook` (or live hot-reload on localhost:8090).
*/
using IntoChat.OS.Host.Apps;
using DigitalBrain;
using DigitalBrain.Neurons;
using DigitalBrain.UI;
using IntoChat.OS.Modules.Apps;
using IntoChat.OS.UI;
using static IntoChat.OS.UI.Kit;

namespace IntoChat.OS.Apps.Widgetbook;

[Signal("widgetbook.open")]
public sealed record WidgetbookOpen(string? StoryPath = null, string? ThemeMode = null) : Signal;

[Signal("widgetbook.switch-source")]
public sealed record WidgetbookSwitchSource(string? Mode = null, string? BaseUrl = null) : Signal;

[Signal("widgetbook.set-theme")]
public sealed record WidgetbookSetTheme(string? ThemeMode = null) : Signal;

[Signal("widgetbook.reload")]
public sealed record WidgetbookReload : Signal;

public sealed record WidgetbookAppState(
    string BaseUrl = "/widgetbook",
    string ActiveStoryPath = "",
    string ThemeMode = "dark",
    WidgetbookSourceMode SourceMode = WidgetbookSourceMode.BundledStatic,
    int ReloadToken = 0)
{
    public WidgetbookAppState() : this("/widgetbook", "", "dark", WidgetbookSourceMode.BundledStatic, 0) { }

    public string CurrentUrl
    {
        get
        {
            var basePath = string.IsNullOrWhiteSpace(BaseUrl) ? "/widgetbook" : BaseUrl.TrimEnd('/');
            var parts = new List<string>();
            if (!string.IsNullOrWhiteSpace(ActiveStoryPath))
            {
                parts.Add($"path={Uri.EscapeDataString(ActiveStoryPath)}");
            }
            if (!string.IsNullOrWhiteSpace(ThemeMode))
            {
                parts.Add($"theme={Uri.EscapeDataString(ThemeMode)}");
            }
            if (ReloadToken > 0)
            {
                parts.Add($"r={ReloadToken}");
            }

            var target = basePath.StartsWith("http", StringComparison.OrdinalIgnoreCase)
                ? basePath
                : $"{basePath}/index.html";
            return parts.Count == 0 ? target : $"{target}?{string.Join('&', parts)}";
        }
    }

    public WidgetbookAppState WithSource(WidgetbookSourceMode mode, string? customBaseUrl = null) => this with
    {
        SourceMode = mode,
        BaseUrl = !string.IsNullOrWhiteSpace(customBaseUrl)
            ? customBaseUrl.Trim()
            : mode == WidgetbookSourceMode.DevHotReload
                ? "http://localhost:8090"
                : "/widgetbook",
    };

    public WidgetbookAppState WithTheme(string themeMode) => this with
    {
        ThemeMode = string.Equals(themeMode?.Trim(), "light", StringComparison.OrdinalIgnoreCase) ? "light" : "dark",
    };

    public WidgetbookAppState Reload() => this with { ReloadToken = ReloadToken + 1 };
}

[Neuron("app.widgetbook")]
[Screen("app.widgetbook", Title = "Widgetbook", Icon = "layout-grid", Category = "Developer", Launch = AppLaunch.Maximized)]
public sealed class WidgetbookApp : EmbeddedWebApp<WidgetbookAppState>
{
    public const string AppName = "widgetbook";

    public static AppBundle Bundle => AppBundle.FromScreen<WidgetbookApp>(
        AppName,
        "1.0.0",
        description: "Open-source interactive UI Kit & Neuron catalog workbench for IntoChat OS.",
        operations:
        [
            new("open", "Open Widgetbook on a story path and theme."),
            new("switch-source", "Switch between bundled /widgetbook and Dev Hot-Reload (localhost:8090)."),
            new("set-theme", "Switch Widgetbook preview theme between dark and light."),
            new("reload", "Reload the active Widgetbook frame."),
            new(UiCommand.SignalAlias, "Dispatch a typed Widgetbook command."),
        ]);

    public new WidgetbookAppState State => base.State;

    public View Render() => PreviewView(State);

    protected override async Task OnActivate(CancellationToken cancellationToken)
    {
        AppActivationScope.Notify(this);
        await base.OnActivate(cancellationToken);
    }

    [On]
    public Task Open(WidgetbookOpen request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var next = State with
        {
            ActiveStoryPath = request.StoryPath?.Trim() ?? State.ActiveStoryPath,
            ThemeMode = string.IsNullOrWhiteSpace(request.ThemeMode) ? State.ThemeMode : State.WithTheme(request.ThemeMode).ThemeMode,
        };
        return Store(next);
    }

    [On]
    public Task SwitchSource(WidgetbookSwitchSource request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var mode = string.Equals(request.Mode?.Trim(), "dev", StringComparison.OrdinalIgnoreCase)
            || string.Equals(request.Mode?.Trim(), "hot-reload", StringComparison.OrdinalIgnoreCase)
            ? WidgetbookSourceMode.DevHotReload
            : WidgetbookSourceMode.BundledStatic;
        return Store(State.WithSource(mode, request.BaseUrl));
    }

    [On]
    public Task SetTheme(WidgetbookSetTheme request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return Store(State.WithTheme(request.ThemeMode ?? "dark"));
    }

    [On]
    public Task Reload(WidgetbookReload request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return Store(State.Reload());
    }

    protected override Node Render(WidgetbookAppState state)
    {
        ArgumentNullException.ThrowIfNull(state);

        return RenderEmbeddedSurface(
            shellKey: "widgetbook-shell",
            browserKey: "widgetbook-browser",
            title: "IntoChat.OS Widgetbook",
            url: state.CurrentUrl,
            toolbar: null);
    }
}
