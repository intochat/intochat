/*
# .NET Aspire Dashboard
Role: Operator
Shipped: true
Operations: open, select-section, focus-trace, connect, reload, ui.command, ui.navigate-to, ui.navigate-from

Built-in full-bleed WebBrowser operator surface (`app.aspire`) targeting the live .NET Aspire Dashboard
for resources, distributed traces (`traces/detail/{traceId}`), structured logs, console logs, and metrics.
*/
using DigitalBrain;
using DigitalBrain.Neurons;
using DigitalBrain.Rules;
using DigitalBrain.UI;
using IntoChat.OS.Framework.Logging;
using IntoChat.OS.Modules.Apps;
using IntoChat.OS.UI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using static IntoChat.OS.UI.Kit;

namespace IntoChat.OS.Apps.Aspire;

[Signal("aspire.open")]
public sealed record AspireOpen(
    string? Section = null,
    string? View = null,
    string? TraceId = null,
    string? BaseUrl = null) : Signal;

[Signal("aspire.select-section")]
public sealed record AspireSelectSection(string? Section = null) : Signal;

[Signal("aspire.focus-trace")]
public sealed record AspireFocusTrace(string? TraceId = null) : Signal;

[Signal("aspire.connect")]
public sealed record AspireConnect(string? BaseUrl = null) : Signal;

[Signal("aspire.reload")]
public sealed record AspireReload : Signal;

[Neuron("app.aspire")]
[Screen("app.aspire", Title = ".NET Aspire", Icon = "activity")]
public sealed class AspireApp : AggregateScreen<AspireAppState>, INavigatedTo<BrowserNavArgs>, INavigatedFrom
{
    public const string AppName = "aspire";

    private static readonly ILogger ConsoleLog = OsConsoleLog.For<AspireApp>();

    private IConfiguration? Configuration { get; }

    private ILogger<AspireApp>? InjectedLogger { get; }

    private ILogger Log => InjectedLogger ?? ConsoleLog;

    public AspireApp() : this(null, null) { }

    [ActivatorUtilitiesConstructor]
    public AspireApp(IConfiguration? configuration = null, ILogger<AspireApp>? logger = null)
    {
        Configuration = configuration;
        InjectedLogger = logger;
    }

    public static AppBundle Bundle { get; } = new(
        AppName,
        "1.0.0",
        new AppManifest(
            Title: ".NET Aspire",
            Description: "Built-in .NET Aspire operator dashboard for resources, distributed traces, structured logs, console logs, and metrics.",
            Operations:
            [
                new("open", "Open the .NET Aspire Dashboard on a section or trace deep-link."),
                new("select-section", "Switch between resources, traces, structuredlogs, consolelogs, and metrics."),
                new("focus-trace", "Deep-link directly to a distributed trace span tree."),
                new("connect", "Connect to a live http/https .NET Aspire Dashboard endpoint."),
                new("reload", "Reload the active .NET Aspire Dashboard frame."),
                new(UiCommand.SignalAlias, "Dispatch a typed Aspire aggregate command."),
                new(UiNavigateTo.SignalAlias, "Navigate into Aspire with typed BrowserNavArgs."),
                new(UiNavigateFrom.SignalAlias, "Evaluate departure rules before leaving Aspire."),
            ],
            Settings: [],
            Slots: [],
            Icon: "activity"),
        "",
        new Dictionary<string, string>(StringComparer.Ordinal))
    {
        CompiledType = typeof(AspireApp).AssemblyQualifiedName,
    };

    public new AspireAppState State => base.State;

    public View Render() => PreviewView(State);

    protected override async Task OnActivate(CancellationToken cancellationToken)
    {
        AppActivationScope.Notify(this);
        await base.OnActivate(cancellationToken);
        Log.LogDebug("Activated AspireApp screen ({Alias}).", SafeScreenLabel());
        var hydrated = HydrateEndpointFromConfiguration(State);
        if (hydrated != State)
        {
            await Show(hydrated);
        }
    }

    public async Task OnNavigatedTo(NavigationArrival<BrowserNavArgs> arrival, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(arrival);
        var basis = HydrateEndpointFromConfiguration(State);
        var decision = basis.ApplyNavigation(arrival.Args, arrival.Transition);
        if (decision.IsAccepted && basis != State && decision.Next == basis)
        {
            await Show(basis);
            return;
        }

        await ApplyDecisionAsync(decision);
    }

    public Rule CanNavigateFrom(NavigationDeparture departure)
    {
        ArgumentNullException.ThrowIfNull(departure);
        return State.CanDepartRule(departure);
    }

    public Task OnNavigatedFrom(NavigationDeparture departure, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(departure);
        return State.ShowConnectBar || (departure.Force && State.NavigationLocked)
            ? Store(State with { ShowConnectBar = false, NavigationLocked = false })
            : Task.CompletedTask;
    }

    [On]
    public async Task Open(AspireOpen request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var basis = HydrateEndpointFromConfiguration(State);
        var requestedSection = !string.IsNullOrWhiteSpace(request.Section) ? request.Section : request.View;
        if (string.IsNullOrWhiteSpace(requestedSection)
            && string.IsNullOrWhiteSpace(request.TraceId)
            && string.IsNullOrWhiteSpace(request.BaseUrl))
        {
            await Show(basis);
            return;
        }

        var nav = new BrowserNavArgs(
            Section: requestedSection,
            TraceId: request.TraceId,
            BaseUrl: request.BaseUrl);
        await ApplyDecisionAsync(basis.ApplyNavigation(nav, ViewTransition.FadeThrough));
    }

    [On]
    public Task SelectSection(AspireSelectSection request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return ApplyDecisionAsync(State.SelectSection(request.Section));
    }

    [On]
    public Task FocusTrace(AspireFocusTrace request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return ApplyDecisionAsync(State.FocusTrace(request.TraceId));
    }

    [On]
    public Task Connect(AspireConnect request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return ApplyDecisionAsync(State.ConnectEndpoint(request.BaseUrl));
    }

    [On]
    public Task Reload(AspireReload request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return Store(State.Reload());
    }

    public static (string BaseUrl, bool IsConfigured) ResolveEndpoint(
        IConfiguration? configuration,
        Func<string, string?>? readEnvironment = null)
    {
        if (configuration is not null)
        {
            foreach (var key in (string[])[AspireAppState.ConfiguredUrlKey, AspireAppState.DashboardUrlEnvKey, AspireAppState.DotnetDashboardUrlsKey])
            {
                if (AspireAppState.TryNormalizeBaseUrl(configuration[key], out var fromConfig))
                {
                    return (fromConfig!, true);
                }
            }

            return (AspireAppState.DefaultBaseUrl, false);
        }

        var env = readEnvironment ?? Environment.GetEnvironmentVariable;
        foreach (var envKey in (string[])["IntoChatOS__AspireDashboardUrl", AspireAppState.DashboardUrlEnvKey, AspireAppState.DotnetDashboardUrlsKey])
        {
            if (AspireAppState.TryNormalizeBaseUrl(env(envKey), out var fromEnv))
            {
                return (fromEnv!, true);
            }
        }

        return (AspireAppState.DefaultBaseUrl, false);
    }

    private AspireAppState HydrateEndpointFromConfiguration(AspireAppState current)
    {
        if (current.IsConfiguredEndpoint)
        {
            return current;
        }

        var (resolvedUrl, isConfigured) = ResolveEndpoint(Configuration);
        return isConfigured
            ? current with
            {
                BaseUrl = resolvedUrl,
                IsConfiguredEndpoint = true,
                Status = $"Aspire Dashboard · {AspireAppState.FormatSectionLabel(current.Section)}",
            }
            : current;
    }

    protected override Node Render(AspireAppState state)
    {
        ArgumentNullException.ThrowIfNull(state);

        var toolbarRow = HStack(
            Space.Xs,
            BuildSectionPill("resources", "Resources", state.Section),
            BuildSectionPill("traces", "Traces", state.Section),
            BuildSectionPill("structuredlogs", "Structured Logs", state.Section),
            BuildSectionPill("consolelogs", "Console", state.Section),
            BuildSectionPill("metrics", "Metrics", state.Section),
            state.TraceId is { Length: > 0 } traceId
                ? StatusPill(StatusKind.Working, $"Trace {AspireAppState.ShortTrace(traceId)}")
                : null,
            FlexSpacer(),
            LastRejection is { } rejection
                ? StatusPill(StatusKind.Error, string.Join(", ", rejection.Rules))
                : null,
            StatusPill(state.IsConfiguredEndpoint ? StatusKind.Done : StatusKind.Idle, state.EndpointBadge),
            GhostButton("Endpoint", Icons.Sliders)
                .Key("aspire:toggle-connect")
                .Small()
                .OnClick<AspireAppState>(s => s.ToggleConnectBar()),
            Button("Reload", Icons.RefreshCw)
                .Key("aspire:reload")
                .Small()
                .OnClick<AspireAppState>(s => s.Reload()));

        Node toolbar = state.ShowConnectBar
            ? VStack(
                Space.Xs,
                toolbarRow,
                HStack(
                    Space.Sm,
                    TextField("Aspire Dashboard URL", AspireAppState.DefaultBaseUrl)
                        .Key("aspire:url-input")
                        .Value(state.BaseUrl)
                        .OnSubmit<AspireAppState>(s => s.ConnectEndpoint(Arg.Text)),
                    GhostButton("Close")
                        .Key("aspire:close-connect")
                        .Small()
                        .OnClick<AspireAppState>(s => s.ToggleConnectBar())))
            : toolbarRow;

        var browser = WebBrowser(
            state.CurrentUrl,
            "Aspire Dashboard",
            key: "aspire-browser",
            height: 920,
            automationName: "Aspire Dashboard");

        return AppShell(
            "aspire-shell",
            sidebar: null,
            body: browser,
            toolbar: toolbar,
            inspector: null,
            automationName: "Aspire Dashboard",
            transition: ActiveTransition);
    }

    private static Node BuildSectionPill(string sectionId, string label, string activeSection)
    {
        var selected = string.Equals(sectionId, activeSection, StringComparison.Ordinal);
        var builder = selected ? PrimaryButton(label) : GhostButton(label);
        return builder
            .Key($"aspire:section:{sectionId}")
            .Small()
            .OnClick<AspireAppState>(s => s.SelectSection(sectionId));
    }

    private string SafeScreenLabel()
    {
        try
        {
            return Id.ToString();
        }
        catch (InvalidOperationException ex)
        {
            Log.LogDebug(ex, "AspireApp neuron ID unavailable during standalone activation.");
            return "app.aspire";
        }
    }
}
