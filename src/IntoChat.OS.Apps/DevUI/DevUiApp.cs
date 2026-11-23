/*
# DevUI Operator Console
Role: Operator
Shipped: true
Operations: open, reload, ui.command

Built-in full-bleed WebBrowser operator surface (`app.devui`) targeting IntoChat OS's co-hosted `/devui/` developer console.
*/
using IntoChat.OS.Host.Apps;
using DigitalBrain;
using DigitalBrain.Neurons;
using DigitalBrain.UI;
using IntoChat.OS.Modules.Apps;
using IntoChat.OS.UI;
using static IntoChat.OS.UI.Kit;

namespace IntoChat.OS.Apps.DevUI;

[Signal("devui.open")]
public sealed record DevUiOpen(string? Path = null) : Signal;

[Signal("devui.reload")]
public sealed record DevUiReload : Signal;

public sealed record DevUiAppState(
    string BaseUrl = "/devui/",
    int ReloadToken = 0)
{
    public DevUiAppState() : this("/devui/", 0) { }

    public string CurrentUrl => ReloadToken > 0
        ? $"{BaseUrl}?r={ReloadToken}"
        : BaseUrl;

    public DevUiAppState Reload() => this with { ReloadToken = ReloadToken + 1 };
}

[Neuron("app.devui")]
[Screen("app.devui", Title = "DevUI", Icon = "terminal")]
public sealed class DevUiApp : EmbeddedWebApp<DevUiAppState>
{
    public const string AppName = "devui";

    public static AppBundle Bundle { get; } = new(
        AppName,
        "1.0.0",
        new AppManifest(
            Title: "DevUI",
            Description: "Built-in developer console and runtime inspector at /devui/.",
            Operations:
            [
                new("open", "Open the co-hosted /devui/ developer console."),
                new("reload", "Reload the active DevUI frame."),
                new(UiCommand.SignalAlias, "Dispatch a typed DevUI command."),
            ],
            Settings: [],
            Slots: [],
            Icon: "terminal"),
        "",
        new Dictionary<string, string>(StringComparer.Ordinal))
    {
        CompiledType = typeof(DevUiApp).AssemblyQualifiedName,
    };

    public new DevUiAppState State => base.State;

    public View Render() => PreviewView(State);

    protected override async Task OnActivate(CancellationToken cancellationToken)
    {
        AppActivationScope.Notify(this);
        await base.OnActivate(cancellationToken);
    }

    [On]
    public Task Open(DevUiOpen request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var path = string.IsNullOrWhiteSpace(request.Path) ? "/devui/" : request.Path.Trim();
        return Store(State with { BaseUrl = path });
    }

    [On]
    public Task Reload(DevUiReload request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return Store(State.Reload());
    }

    protected override Node Render(DevUiAppState state)
    {
        ArgumentNullException.ThrowIfNull(state);

        var toolbar = HStack(
            Space.Xs,
            StatusPill(StatusKind.Done, "/devui/ · Co-Hosted"),
            FlexSpacer(),
            Button("Reload", Icons.RefreshCw)
                .Key("devui:reload")
                .Small()
                .OnClick<DevUiAppState>(s => s.Reload()));

        return RenderEmbeddedSurface(
            shellKey: "devui-shell",
            browserKey: "devui-browser",
            title: "IntoChat.OS DevUI",
            url: state.CurrentUrl,
            toolbar: toolbar);
    }
}
