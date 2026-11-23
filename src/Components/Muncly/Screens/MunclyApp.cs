/*
# Muncly CRM
Role: Operator
Shipped: true
Operations: open, connect, reload, ui.command, ui.navigate-to, ui.navigate-from

Built-in full-bleed WebBrowser app (`app.muncly`) targeting Muncly CRM with stored web credentials
and paired MCP server access (`muncly-crm`) for AI agents.
*/
using DigitalBrain;
using DigitalBrain.Neurons;
using DigitalBrain.Rules;
using DigitalBrain.UI;
using IntoChat.OS.Host.Apps;
using IntoChat.OS.Host.Logging;
using IntoChat.OS.Modules.Apps;
using IntoChat.OS.UI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace IntoChat.Muncly.Screens;

[Neuron("app.muncly")]
[Screen("app.muncly", Title = "Muncly CRM", Icon = "muncly")]
public sealed class MunclyApp : EmbeddedWebApp<MunclyAppState>, INavigatedTo<BrowserNavArgs>, INavigatedFrom
{
    public const string AppName = "muncly";

    private static readonly ILogger ConsoleLog = OsConsoleLog.For<MunclyApp>();

    private IConfiguration? Configuration { get; }
    private ILogger<MunclyApp>? InjectedLogger { get; }
    private ILogger Log => InjectedLogger ?? ConsoleLog;

    public MunclyApp() : this(null, null) { }

    [ActivatorUtilitiesConstructor]
    public MunclyApp(IConfiguration? configuration = null, ILogger<MunclyApp>? logger = null)
    {
        Configuration = configuration;
        InjectedLogger = logger;
    }

    public static AppBundle Bundle { get; } = new(
        AppName,
        "1.0.0",
        new AppManifest(
            Title: "Muncly CRM",
            Description: "Muncly CRM workspace with embedded web app and authenticated MCP access for agents.",
            Operations:
            [
                new("open", "Open Muncly CRM at the root or a specific object/record deep-link."),
                new("connect", "Connect to a Muncly CRM https endpoint."),
                new("reload", "Reload the active Muncly CRM frame."),
                new(UiCommand.SignalAlias, "Dispatch a typed Muncly aggregate command."),
                new(UiNavigateTo.SignalAlias, "Navigate into Muncly CRM with typed BrowserNavArgs."),
                new(UiNavigateFrom.SignalAlias, "Evaluate departure rules before leaving Muncly CRM."),
            ],
            Settings: [],
            Slots: [],
            Icon: "muncly"),
        "",
        new Dictionary<string, string>(StringComparer.Ordinal))
    {
        CompiledType = typeof(MunclyApp).AssemblyQualifiedName,
    };

    public new MunclyAppState State => base.State;

    public View Render() => PreviewView(State);

    protected override async Task OnActivate(CancellationToken cancellationToken)
    {
        AppActivationScope.Notify(this);
        await base.OnActivate(cancellationToken);
        Log.LogDebug("Activated MunclyApp screen.");
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
        return departure.Force && State.NavigationLocked
            ? Store(State with { NavigationLocked = false })
            : Task.CompletedTask;
    }

    [On]
    public Task Open(MunclyOpen request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var basis = HydrateEndpointFromConfiguration(State);
        var path = !string.IsNullOrWhiteSpace(request.Path) ? request.Path : request.Input;
        return ApplyDecisionAsync(basis.OpenPath(path, request.ObjectName, request.RecordId, request.BaseUrl));
    }

    [On]
    public Task Connect(MunclyConnect request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return ApplyDecisionAsync(State.ConnectEndpoint(request.BaseUrl));
    }

    [On]
    public Task Reload(MunclyReload _)
        => ApplyDecisionAsync(State.Reload());

    protected override Node Render(MunclyAppState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        return RenderEmbeddedSurface(
            shellKey: "muncly-shell",
            browserKey: "muncly-browser",
            title: "Muncly CRM",
            url: state.CurrentUrl,
            toolbar: null,
            transition: state.LastTransition);
    }

    private MunclyAppState HydrateEndpointFromConfiguration(MunclyAppState state)
    {
        if (state.IsConfiguredEndpoint)
        {
            return state;
        }

        var configured = FirstValidEndpoint(
            Configuration?[MunclyAppState.ConfiguredUrlKey],
            Configuration?[MunclyAppState.IntegrationUrlKey],
            Configuration?[MunclyAppState.EnvUrlKey],
            Environment.GetEnvironmentVariable(MunclyAppState.EnvUrlKey));

        if (configured is null)
        {
            return state;
        }

        var normalized = MunclyAppState.NormalizeBaseUrlOrFallback(configured);
        return state with
        {
            BaseUrl = normalized,
            IsConfiguredEndpoint = true,
            Status = $"Muncly CRM · {new Uri(normalized).Host}",
        };
    }

    private static string? FirstValidEndpoint(params string?[] candidates)
    {
        foreach (var candidate in candidates)
        {
            if (MunclyAppState.IsValidEndpoint(candidate))
            {
                return candidate!.Trim();
            }
        }
        return null;
    }
}
