/*
# Orleans Grain Dashboard
Role: Operator
Shipped: true
Operations: open, select-section, filter-grain, reload, ui.command, ui.navigate-to, ui.navigate-from

Built-in full-bleed WebBrowser operator surface (`app.orleans`) targeting IntoChat OS's co-hosted `/orleans`
Grain Dashboard (`Overview`, `Grains`, `Silos`, `Reminders`, and grain-type deep-links).
*/
using DigitalBrain;
using DigitalBrain.Neurons;
using DigitalBrain.Rules;
using DigitalBrain.UI;
using IntoChat.OS.Modules.Apps;
using IntoChat.OS.UI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using IntoChat.OS.Host.Logging;
using static IntoChat.OS.UI.Kit;

namespace IntoChat.OS.Apps.Orleans;

[Signal("orleans.open")]
public sealed record OrleansOpen(
    string? Section = null,
    string? View = null,
    string? GrainFilter = null,
    string? BaseUrl = null,
    string? Input = null) : Signal;

[Signal("orleans.select-section")]
public sealed record OrleansSelectSection(string? Section = null) : Signal;

[Signal("orleans.filter-grain")]
public sealed record OrleansFilterGrain(string? GrainFilter = null) : Signal;

[Signal("orleans.reload")]
public sealed record OrleansReload : Signal;

[Neuron("app.orleans")]
[Screen("app.orleans", Title = "Orleans Dashboard", Icon = "cpu")]
public sealed class OrleansApp : AggregateScreen<OrleansAppState>, INavigatedTo<BrowserNavArgs>, INavigatedFrom
{
    public const string AppName = "orleans";

    private static readonly ILogger ConsoleLog = OsConsoleLog.For<OrleansApp>();

    private ILogger<OrleansApp>? InjectedLogger { get; }

    private ILogger Log => InjectedLogger ?? ConsoleLog;

    public OrleansApp() : this(null) { }

    [ActivatorUtilitiesConstructor]
    public OrleansApp(ILogger<OrleansApp>? logger = null)
        => InjectedLogger = logger;

    public static AppBundle Bundle { get; } = new(
        AppName,
        "1.0.0",
        new AppManifest(
            Title: "Orleans Dashboard",
            Description: "Co-hosted Microsoft Orleans grain, silo, activation, method-profile, and reminder dashboard.",
            Operations:
            [
                new("open", "Open the co-hosted /orleans Grain Dashboard on a section or grain filter."),
                new("select-section", "Switch between overview, grains, silos, and reminders."),
                new("filter-grain", "Focus the Grains view on a specific grain type or activation filter."),
                new("reload", "Reload the active Orleans Grain Dashboard frame."),
                new(UiCommand.SignalAlias, "Dispatch a typed Orleans aggregate command."),
                new(UiNavigateTo.SignalAlias, "Navigate into Orleans with typed BrowserNavArgs."),
                new(UiNavigateFrom.SignalAlias, "Evaluate departure rules before leaving Orleans."),
            ],
            Settings: [],
            Slots: [],
            Icon: "cpu"),
        "",
        new Dictionary<string, string>(StringComparer.Ordinal))
    {
        CompiledType = typeof(OrleansApp).AssemblyQualifiedName,
    };

    public new OrleansAppState State => base.State;

    public View Render() => PreviewView(State);

    protected override async Task OnActivate(CancellationToken cancellationToken)
    {
        AppActivationScope.Notify(this);
        await base.OnActivate(cancellationToken);
        Log.LogDebug("Activated OrleansApp screen ({Alias}).", SafeScreenLabel());
    }

    public Task OnNavigatedTo(NavigationArrival<BrowserNavArgs> arrival, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(arrival);
        return ApplyDecisionAsync(State.ApplyNavigation(arrival.Args, arrival.Transition));
    }

    public Rule CanNavigateFrom(NavigationDeparture departure)
    {
        ArgumentNullException.ThrowIfNull(departure);
        return State.CanDepartRule(departure);
    }

    public Task OnNavigatedFrom(NavigationDeparture departure, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(departure);
        return departure.Force && State.NavigationLocked
            ? Store(State with { NavigationLocked = false })
            : Task.CompletedTask;
    }

    [On]
    public Task Open(OrleansOpen request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var requestedSection = !string.IsNullOrWhiteSpace(request.Section) ? request.Section : request.View;
        if (string.IsNullOrWhiteSpace(requestedSection)
            && string.IsNullOrWhiteSpace(request.GrainFilter)
            && string.IsNullOrWhiteSpace(request.BaseUrl)
            && string.IsNullOrWhiteSpace(request.Input))
        {
            return Show(State);
        }

        var nav = new BrowserNavArgs(
            Section: requestedSection,
            GrainFilter: request.GrainFilter,
            Route: request.Input,
            BaseUrl: request.BaseUrl);
        return ApplyDecisionAsync(State.ApplyNavigation(nav, ViewTransition.FadeThrough));
    }

    [On]
    public Task SelectSection(OrleansSelectSection request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return ApplyDecisionAsync(State.SelectSection(request.Section));
    }

    [On]
    public Task FilterGrain(OrleansFilterGrain request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return ApplyDecisionAsync(State.FilterByGrain(request.GrainFilter));
    }

    [On]
    public Task Reload(OrleansReload request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return Store(State.Reload());
    }

    protected override Node Render(OrleansAppState state)
    {
        ArgumentNullException.ThrowIfNull(state);

        var toolbar = HStack(
            Space.Xs,
            BuildSectionPill("overview", "Overview", state.Section),
            BuildSectionPill("grains", "Grains", state.Section),
            BuildSectionPill("silos", "Silos", state.Section),
            BuildSectionPill("reminders", "Reminders", state.Section),
            state.GrainFilter is { Length: > 0 } filter
                ? StatusPill(StatusKind.Working, $"Grain: {filter}")
                : null,
            state.GrainFilter is { Length: > 0 }
                ? GhostButton("Clear")
                    .Key("orleans:clear-filter")
                    .Small()
                    .OnClick<OrleansAppState>(s => s.ClearGrainFilter())
                    .ToNode()
                : null,
            FlexSpacer(),
            LastRejection is { } rejection
                ? StatusPill(StatusKind.Error, string.Join(", ", rejection.Rules))
                : null,
            StatusPill(StatusKind.Done, "/orleans · Co-Hosted"),
            Button("Reload", Icons.RefreshCw)
                .Key("orleans:reload")
                .Small()
                .OnClick<OrleansAppState>(s => s.Reload()));

        var browser = WebBrowser(
            state.CurrentUrl,
            "Orleans Grain Dashboard",
            key: "orleans-browser",
            height: 920,
            automationName: "Orleans Grain Dashboard",
            chrome: "frameless");

        return AppShell(
            "orleans-shell",
            sidebar: null,
            body: browser,
            toolbar: toolbar,
            inspector: null,
            automationName: "Orleans Grain Dashboard",
            transition: ActiveTransition,
            edgeToEdge: true);
    }

    private static Node BuildSectionPill(string sectionId, string label, string activeSection)
    {
        var selected = string.Equals(sectionId, activeSection, StringComparison.Ordinal);
        var builder = selected ? PrimaryButton(label) : GhostButton(label);
        return builder
            .Key($"orleans:section:{sectionId}")
            .Small()
            .OnClick<OrleansAppState>(s => s.SelectSection(sectionId));
    }

    private string SafeScreenLabel()
    {
        try
        {
            return Id.ToString();
        }
        catch (InvalidOperationException ex)
        {
            Log.LogDebug(ex, "OrleansApp neuron ID unavailable during standalone activation.");
            return "app.orleans";
        }
    }
}
