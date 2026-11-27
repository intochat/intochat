/*
# Muncly CRM
Role: Operator
Shipped: true
Operations: open, connect, reload, probe, disconnect, toggle-inspector, ui.navigate-to, ui.navigate-from

Built-in Muncly CRM workspace app (`app.muncly`) bound directly to `{brain}/connection/mcp/muncly-crm`
with server-side FormControl onboarding, live MCP tool inspection, and iframe-safe browser launch.
*/
using System.Text.Json;
using DigitalBrain;
using DigitalBrain.Capabilities.Connections;
using DigitalBrain.Capabilities.Secrets;
using DigitalBrain.Components;
using DigitalBrain.Contracts;
using DigitalBrain.Neurons;
using DigitalBrain.Rules;
using DigitalBrain.UI;
using DigitalBrain.UI.Commands;
using IntoChat.OS.Host.Apps;
using IntoChat.OS.Host.Logging;
using IntoChat.OS.Modules.Apps;
using IntoChat.OS.Modules.Identity.Contracts.Connections;
using IntoChat.OS.Modules.Mcp;
using IntoChat.OS.UI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using static IntoChat.OS.UI.Kit;

namespace IntoChat.Muncly.Screens;

[Neuron("app.muncly")]
[Screen("app.muncly", Title = "Muncly CRM", Icon = "muncly", Category = "Business", Launch = AppLaunch.Maximized)]
public sealed partial class MunclyApp : EmbeddedWebApp<MunclyAppState>, INavigatedTo<BrowserNavArgs>, INavigatedFrom
{
    public const string AppName = "muncly";
    public const string ConnectFormKey = "muncly-connect";

    private static readonly ILogger ConsoleLog = OsConsoleLog.For<MunclyApp>();

    [Slot(Required = false)] private partial SlotCollection<IMcpToolConnection> McpServers { get; }
    [Slot(Required = false)] private partial SlotCollection<IConnection> Connections { get; }

    private IConfiguration? Configuration { get; }
    private ISecretVaultPort? SecretVaultPort { get; }
    private IConnectionSecretBinder? ConnectionSecretBinder { get; }
    private ILogger<MunclyApp>? InjectedLogger { get; }
    private ILogger Log => InjectedLogger ?? ConsoleLog;

    public MunclyApp() : this(null, null, null, null) { }

    [ActivatorUtilitiesConstructor]
    public MunclyApp(
        IConfiguration? configuration = null,
        ISecretVaultPort? secretVaultPort = null,
        ILogger<MunclyApp>? logger = null,
        IConnectionSecretBinder? connectionSecretBinder = null)
    {
        Configuration = configuration;
        SecretVaultPort = secretVaultPort;
        InjectedLogger = logger;
        ConnectionSecretBinder = connectionSecretBinder;
    }

    public static AppBundle Bundle => AppBundle.FromScreen<MunclyApp>(
        AppName,
        "2.0.0",
        description: "Muncly CRM workspace with embedded web app and authenticated MCP access for agents.",
        operations:
        [
            new("open", "Open Muncly CRM at the root or a specific object/record deep-link."),
            new("connect", "Connect to a Muncly CRM https endpoint."),
            new("reload", "Reload the active Muncly CRM frame."),
            new("probe", "Probe the live Muncly CRM MCP connection."),
            new("disconnect", "Disconnect Muncly CRM from this brain."),
            new("toggle-inspector", "Toggle the live MCP tools inspector panel."),
            new(UiNavigateTo.SignalAlias, "Navigate into Muncly CRM with typed BrowserNavArgs."),
            new(UiNavigateFrom.SignalAlias, "Evaluate departure rules before leaving Muncly CRM."),
        ]);

    public new MunclyAppState State => base.State;

    public View Render() => PreviewView(State);

    protected override async Task OnActivate(CancellationToken cancellationToken)
    {
        AppActivationScope.Notify(this);
        await base.OnActivate(cancellationToken);
        await FollowChild<IMunclyFormControl, Submitted>(ConnectFormKey);
        Log.LogDebug("Activated MunclyApp screen.");
        var hydrated = await HydrateFromConnectionAsync(State, cancellationToken);
        if (hydrated != State)
        {
            await Show(hydrated);
        }
    }

    public async Task OnNavigatedTo(NavigationArrival<BrowserNavArgs> arrival, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(arrival);
        var basis = await HydrateFromConnectionAsync(State, cancellationToken);
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
    public async Task OnSubmitted(Submitted submitted)
    {
        ArgumentNullException.ThrowIfNull(submitted);
        if (!string.Equals(SignalChild, ConnectFormKey, StringComparison.Ordinal))
        {
            return;
        }

        var rawUrl = submitted.Values.TryGetValue("url", out var u) && !string.IsNullOrWhiteSpace(u)
            ? u.Trim()
            : MunclyAppState.DefaultBaseUrl;
        var tokenRef = submitted.SecretRefs is not null && submitted.SecretRefs.TryGetValue("token", out var tRef)
            ? tRef
            : null;

        await ConnectViaMcpConnectionAsync(rawUrl, tokenRef, CancellationToken.None);
    }

    [On]
    public async Task Open(MunclyOpen request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var basis = await HydrateFromConnectionAsync(State, CancellationToken.None);
        var path = !string.IsNullOrWhiteSpace(request.Path) ? request.Path : request.Input;
        await ApplyDecisionAsync(basis.OpenPath(path, request.ObjectName, request.RecordId, request.BaseUrl));
    }

    [On]
    public Task Connect(MunclyConnect request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return ConnectViaMcpConnectionAsync(request.BaseUrl ?? request.Value, tokenRef: null, CancellationToken.None);
    }

    [On]
    public Task Reload(MunclyReload _)
        => ApplyDecisionAsync(State.Reload());

    [On]
    public async Task Probe(MunclyProbe _)
    {
        if (TryConnectionId() is { } connId)
        {
            try
            {
                var probed = await Connections[connId].Probe();
                var hydrated = await HydrateFromConnectionAsync(State with
                {
                    ConnectionStatus = probed.Status,
                    ConnectionDetail = probed.Detail,
                    Status = $"Muncly CRM · {probed.Status}",
                }, CancellationToken.None);
                await Show(hydrated);
                return;
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                Log.LogDebug(error, "IConnection.Probe failed for Muncly CRM.");
                await Show(State with
                {
                    ConnectionStatus = ConnectionStatus.Failing,
                    ConnectionDetail = error.Message,
                    Status = $"Muncly CRM probe failed: {error.Message}",
                });
                return;
            }
        }

        await Show(State with { Status = "Muncly CRM · Probe unavailable in detached preview." });
    }

    [On]
    public async Task Disconnect(MunclyDisconnect _)
    {
        if (TryConnectionId() is { } connId)
        {
            try
            {
                await Connections[connId].Disconnect();
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                Log.LogDebug(error, "IConnection.Disconnect failed for Muncly CRM.");
            }

            try
            {
                await McpServers[connId].DisconnectServer();
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                Log.LogDebug(error, "IMcpToolConnection.DisconnectServer failed for Muncly CRM.");
            }
        }

        await Show(State with
        {
            IsConfiguredEndpoint = false,
            ConnectionStatus = ConnectionStatus.Disconnected,
            DiscoveredTools = [],
            ConnectionDetail = null,
            ShowToolsInspector = false,
            Status = "Muncly CRM disconnected.",
        });
    }

    [On]
    public Task ToggleInspector(MunclyToggleInspector _)
        => Show(State with { ShowToolsInspector = !State.ShowToolsInspector });

    protected override Node Render(MunclyAppState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        if (!state.IsConfiguredEndpoint)
        {
            var form = Child<IMunclyFormControl, FormProps>(ConnectFormKey, new FormProps(
                [
                    new FormInput("url", "CRM URL", Value: MunclyAppState.DefaultBaseUrl, Placeholder: MunclyAppState.DefaultBaseUrl, MaxLength: 512),
                    new FormInput("token", "MCP Bearer Token", FormFieldKind.Secret, Placeholder: "crm_…"),
                ],
                "Connect Muncly CRM",
                AutomationName: "Connect Muncly CRM"));

            var setupCard = Card(
                "Connect Muncly CRM",
                "Connect your Muncly CRM workspace and MCP tools on connection/mcp/muncly-crm. Your Bearer token is sealed in your vault via SecretRef.")
                .Style(OsMixStyles.Card())[
                VStack(Space.Md)[
                    !string.IsNullOrWhiteSpace(state.ConnectionDetail)
                        ? Banner("Connection notice", state.ConnectionDetail!, tone: "error")
                        : null,
                    form
                ]
            ];

            return AppShell(
                "muncly-shell",
                sidebar: null,
                body: setupCard,
                toolbar: null,
                inspector: null,
                automationName: "Muncly CRM",
                transition: state.LastTransition);
        }

        var statusKind = state.ConnectionStatus switch
        {
            ConnectionStatus.Connected => StatusKind.Done,
            ConnectionStatus.Expired => StatusKind.NeedsYou,
            ConnectionStatus.Failing => StatusKind.Error,
            _ => StatusKind.Idle,
        };
        var statusLabel = state.ConnectionStatus switch
        {
            ConnectionStatus.Connected => $"Connected · {state.DiscoveredTools.Length} MCP tool(s)",
            ConnectionStatus.Expired => "Expired",
            ConnectionStatus.Failing => "Failing",
            _ => "Configured",
        };

        var toolbar = HStack(Space.Sm)[
            StatusPill(statusKind, statusLabel),
            MutedText(state.CurrentUrl),
            FlexSpacer(),
            PrimaryButton("Open Muncly CRM in Browser ↗", "external-link")
                .Key("muncly:open-browser")
                .Small()
                .WithIntent(IntentKind.OpenWindow, state.CurrentUrl),
            Button(state.ShowToolsInspector ? "Hide MCP Tools" : $"MCP Tools ({state.DiscoveredTools.Length})", "wrench")
                .Key("muncly:toggle-inspector")
                .Small()
                .Sends(new MunclyToggleInspector()),
            Button("Re-probe", Icons.RefreshCw)
                .Key("muncly:probe")
                .Small()
                .Sends(new MunclyProbe()),
            DangerButton("Disconnect", Icons.Trash2)
                .Key("muncly:disconnect")
                .Small()
                .Sends(new MunclyDisconnect())
        ];

        Node? inspector = state.ShowToolsInspector
            ? RenderToolsInspector(state, statusKind, statusLabel)
            : null;

        var browser = WebBrowser(
            state.CurrentUrl,
            "Muncly CRM",
            key: "muncly-browser",
            height: 920,
            executionMode: "wasm-ui",
            automationName: "Muncly CRM",
            chrome: "frameless");

        return AppShell(
            "muncly-shell",
            sidebar: null,
            body: browser,
            toolbar: toolbar,
            inspector: inspector,
            automationName: "Muncly CRM",
            transition: state.LastTransition,
            edgeToEdge: true);
    }

    private static Node RenderToolsInspector(MunclyAppState state, StatusKind statusKind, string statusLabel)
    {
        var toolRows = state.DiscoveredTools.Length > 0
            ? state.DiscoveredTools.Select(static t => (IReadOnlyList<string>)[t]).ToArray()
            : [["No MCP tools discovered yet"]];

        return Card("Connected CRM & Live MCP Tools Inspector", "If your browser blocks embedded iframes (X-Frame-Options: DENY), launch Muncly CRM directly in a top-level tab.")
            .Style(OsMixStyles.Card())[
            VStack(Space.Sm)[
                HStack(Space.Sm)[
                    StatusPill(statusKind, statusLabel),
                    PrimaryButton("Open Muncly CRM in Browser ↗", "external-link")
                        .Key("muncly:inspector-open-browser")
                        .Small()
                        .WithIntent(IntentKind.OpenWindow, state.CurrentUrl)
                ],
                Table(
                    "Connection details",
                    ["Field", "Value"],
                    [
                        ["CRM URL", state.CurrentUrl],
                        ["Connection", $"connection/mcp/{MunclyOptions.DefaultMcpServerName}"],
                        ["Status", statusLabel],
                    ]),
                Table("Discovered MCP tools", ["Tool"], toolRows)
            ]
        ];
    }

    private async Task ConnectViaMcpConnectionAsync(string? rawUrl, string? tokenRef, CancellationToken ct)
    {
        var decision = State.ConnectEndpoint(rawUrl);
        if (!decision.IsAccepted)
        {
            await ApplyDecisionAsync(decision);
            return;
        }

        var next = decision.Next!;
        var mcpUrl = MunclyAppState.BuildMcpEndpointUrl(rawUrl!);
        var connStatus = ConnectionStatus.Connected;
        string[] tools = next.DiscoveredTools;
        string? detail = null;

        if (TryConnectionId() is { } connId)
        {
            try
            {
                McpServerState? serverState = null;
                if (ConnectionSecretBinder is { } binder && !string.IsNullOrWhiteSpace(tokenRef))
                {
                    var fullConnectionKey = ConnectionKeys.For(connId.Brain.Value, IMcpToolConnection.TypeName, MunclyOptions.DefaultMcpServerName);
                    var connView = await binder.BindAsync(
                        connId.Brain,
                        IMcpToolConnection.TypeName,
                        fullConnectionKey,
                        "bearer-token",
                        tokenRef,
                        formatValue: token => SerializeMcpBearerPayload(mcpUrl, token),
                        probe: true,
                        ct);
                    connStatus = connView.Status;
                    detail = connView.Detail;
                    try
                    {
                        serverState = await McpServers[connId].ReadState(ct);
                    }
                    catch (Exception readError) when (readError is not OperationCanceledException)
                    {
                        Log.LogDebug(readError, "IMcpToolConnection.ReadState after Muncly binder BindAsync failed.");
                    }
                }
                else if (!string.IsNullOrWhiteSpace(tokenRef))
                {
                    var bearerToken = await TryResolveSecretRefAsync(tokenRef, ct);
                    if (string.IsNullOrWhiteSpace(bearerToken))
                    {
                        throw new InvalidOperationException("Failed to unseal Muncly Bearer token from vault.");
                    }

                    var payload = SerializeMcpBearerPayload(mcpUrl, bearerToken);
                    var connView = await Connections[connId].Connect("bearer-token", payload);
                    try
                    {
                        connView = await Connections[connId].Probe();
                    }
                    catch (Exception probeError) when (probeError is not OperationCanceledException)
                    {
                        Log.LogDebug(probeError, "IConnection.Probe after Muncly Connect failed.");
                    }

                    connStatus = connView.Status;
                    detail = connView.Detail;
                    try
                    {
                        serverState = await McpServers[connId].ReadState(ct);
                    }
                    catch (Exception readError) when (readError is not OperationCanceledException)
                    {
                        Log.LogDebug(readError, "IMcpToolConnection.ReadState after Muncly Connect failed.");
                    }
                }
                else
                {
                    serverState = await McpServers[connId].ConnectServer(new McpConnectionConfig(mcpUrl, "Muncly CRM"), ct);
                    connStatus = serverState.Connected ? ConnectionStatus.Connected : ConnectionStatus.Failing;
                    detail = serverState.Error;
                }

                if (serverState is not null)
                {
                    tools = (serverState.Tools ?? []).Select(static t => t.Name).Where(static n => !string.IsNullOrWhiteSpace(n)).ToArray();
                    if (serverState.Connected)
                    {
                        connStatus = ConnectionStatus.Connected;
                    }
                }
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                Log.LogDebug(error, "Connecting Muncly MCP connection neuron failed; keeping endpoint state.");
                connStatus = ConnectionStatus.Failing;
                detail = error.Message;
            }
        }

        await Show(next with
        {
            ConnectionStatus = connStatus,
            DiscoveredTools = tools,
            ConnectionDetail = detail,
            Status = connStatus == ConnectionStatus.Connected
                ? $"Muncly CRM · Connected ({tools.Length} tool(s))"
                : $"Muncly CRM · {connStatus}",
        });
    }

    private async Task<MunclyAppState> HydrateFromConnectionAsync(MunclyAppState state, CancellationToken ct)
    {
        if (TryConnectionId() is { } connId)
        {
            try
            {
                var mcpState = await McpServers[connId].ReadState(ct);
                if (!string.IsNullOrWhiteSpace(mcpState.Url) && MunclyAppState.IsValidEndpoint(mcpState.Url))
                {
                    var normalizedOrigin = MunclyAppState.NormalizeBaseUrlOrFallback(mcpState.Url);
                    var tools = (mcpState.Tools ?? [])
                        .Select(static t => t.Name)
                        .Where(static n => !string.IsNullOrWhiteSpace(n))
                        .ToArray();
                    var status = mcpState.Connected
                        ? ConnectionStatus.Connected
                        : !string.IsNullOrWhiteSpace(mcpState.Error)
                            ? ConnectionStatus.Failing
                            : ConnectionStatus.Disconnected;

                    return state with
                    {
                        BaseUrl = normalizedOrigin,
                        IsConfiguredEndpoint = mcpState.Connected || state.IsConfiguredEndpoint,
                        ConnectionStatus = status,
                        DiscoveredTools = tools,
                        ConnectionDetail = mcpState.Error,
                        Status = mcpState.Connected
                            ? $"Muncly CRM · Connected ({tools.Length} tool(s))"
                            : $"Muncly CRM · {new Uri(normalizedOrigin).Host}",
                    };
                }
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                Log.LogDebug(error, "IMcpToolConnection.ReadState is optional when hydrating MunclyApp.");
            }
        }

        if (!state.IsConfiguredEndpoint)
        {
            var configured = Configuration?[MunclyAppState.ConfiguredUrlKey];
            if (MunclyAppState.IsValidEndpoint(configured))
            {
                var normalized = MunclyAppState.NormalizeBaseUrlOrFallback(configured);
                return state with
                {
                    BaseUrl = normalized,
                    IsConfiguredEndpoint = true,
                    Status = $"Muncly CRM · {new Uri(normalized).Host}",
                };
            }
        }

        return state;
    }

    private async Task<string?> TryResolveSecretRefAsync(string? secretRef, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(secretRef) || SecretVaultPort is null)
        {
            return null;
        }

        try
        {
            var resolved = await SecretVaultPort.Resolve(
                new SecretRef { Reference = secretRef.Trim(), Label = "MCP Bearer Token", Status = SecretStatus.Set },
                ct);
            return resolved.Status == SecretVaultStatus.Ok && !string.IsNullOrWhiteSpace(resolved.Value)
                ? resolved.Value.Trim()
                : null;
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            Log.LogDebug(error, "ISecretVaultPort.Resolve failed for Muncly Bearer token.");
            return null;
        }
    }

    private static string SerializeMcpBearerPayload(string mcpUrl, string token)
        => JsonSerializer.Serialize(new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["url"] = mcpUrl,
            ["title"] = "Muncly CRM",
            ["header"] = "Authorization",
            ["prefix"] = "Bearer ",
            ["token"] = token,
        });

    private NeuronId? TryConnectionId()
    {
        var brain = ResolveBrainId(error => Log.LogDebug(error, "Muncly brain id is unavailable during detached preview."));
        return brain is { Value.Length: > 0 } b
            ? IMcpToolConnection.NeuronIdOf(b, MunclyOptions.DefaultMcpServerName)
            : null;
    }
}
