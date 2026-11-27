using DigitalBrain.Capabilities.Connections;
using DigitalBrain.UI;
using IntoChat.OS.Modules.Apps;
using IntoChat.OS.UI;
using static IntoChat.OS.UI.Kit;

namespace IntoChat.OS.Apps.Settings;

/// <summary>
/// Bounded section renderer for Connections (OAuth, GitHub, Muncly, MCP),
/// Files mounts, API Keys vault, and Variables &amp; Secrets.
/// </summary>
internal static class SettingsConnectionsAndKeysView
{
    public static Node RenderConnectionsSection(SettingsAppState state, string? subPage, SettingsChildren children)
    {
        var active = (state.ActiveConnections ?? [])
            .OrderBy(static c => c.Key, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (!string.IsNullOrWhiteSpace(subPage))
        {
            var matched = active.FirstOrDefault(c =>
                string.Equals(c.ConnectorId, subPage, StringComparison.OrdinalIgnoreCase)
                || string.Equals(c.ConnectionId, subPage, StringComparison.OrdinalIgnoreCase)
                || string.Equals(c.Key, subPage, StringComparison.OrdinalIgnoreCase));
            var statusText = matched?.Status.ToString() ?? "Not connected";
            return SettingsViewRenderer.DetailSubPage(
                $"Connections / {subPage}",
                $"ConnectionPage · {subPage}",
                "Live connection status, endpoint, and capability bindings.",
                [
                    ["Connector", matched?.ConnectorId ?? subPage],
                    ["Neuron URI", $"neuron://local/{matched?.Key ?? $"connection/{subPage}"}"],
                    ["Status", statusText],
                    ["Account", matched?.Account ?? "Not configured"],
                ],
                "conn:back",
                "Back to Connections");
        }

        var offers = state.AvailableConnectors is { Length: > 0 }
            ? state.AvailableConnectors
            :
            [
                new SettingsConnectorOffer("github", "GitHub", ["device", "oauth", "pat"], "GitHub"),
                new SettingsConnectorOffer("muncly", "Muncly CRM", ["bearer-token"], "Muncly CRM"),
                new SettingsConnectorOffer("google-drive", "Google Drive", ["oauth"], "Google Drive"),
                new SettingsConnectorOffer("gmail", "Gmail", ["oauth"], "Gmail"),
                new SettingsConnectorOffer("salesforce", "Salesforce", ["oauth"], "Salesforce"),
                new SettingsConnectorOffer("mcp", "MCP Servers", ["bearer-token"], "MCP"),
            ];

        var webhookBase = string.IsNullOrWhiteSpace(state.WebhookBaseUrl)
            ? SettingsApp.DefaultWebhookBaseUrl
            : state.WebhookBaseUrl.TrimEnd('/');
        var webhookUrl = $"{webhookBase}/integrations/github/webhook";
        var ghForwardCommand = $"gh webhook forward --repo <owner/repo> --events=issues,issue_comment,pull_request,check_run,check_suite --url={webhookUrl}";
        var copyWebhookIntent = AppSignals.Copy(webhookUrl);
        var copyGhForwardIntent = AppSignals.Copy(ghForwardCommand);

        var activeRows = active.Select(static conn =>
        {
            var statusKind = conn.Status switch
            {
                ConnectionStatus.Connected => StatusKind.Done,
                ConnectionStatus.Expired => StatusKind.NeedsYou,
                ConnectionStatus.Failing => StatusKind.Error,
                _ => StatusKind.Idle,
            };
            var statusLabel = conn.Status switch
            {
                ConnectionStatus.Connected when conn.ToolCount > 0 => $"Connected · {conn.ToolCount} tools",
                ConnectionStatus.Connected => "Connected",
                ConnectionStatus.Expired => "Expired",
                ConnectionStatus.Failing => string.IsNullOrWhiteSpace(conn.Detail) ? "Failing" : $"Failing · {conn.Detail}",
                _ => "Disconnected",
            };
            var subtitle = !string.IsNullOrWhiteSpace(conn.Account)
                ? conn.Account!
                : !string.IsNullOrWhiteSpace(conn.Endpoint)
                    ? conn.Endpoint!
                    : conn.Key;

            return (Node)HStack(Space.Sm)[
                Text(conn.DisplayName),
                MutedText(subtitle),
                FlexSpacer(),
                StatusPill(statusKind, statusLabel),
                Button("Probe", Icons.RefreshCw)
                    .Key($"conn:probe:{conn.ConnectorId}:{conn.ConnectionId}")
                    .Small()
                    .Sends(new SettingsProbeConnection(conn.ConnectorId, conn.ConnectionId)),
                DangerButton("Disconnect", Icons.Trash2)
                    .Key($"conn:disconnect:{conn.ConnectorId}:{conn.ConnectionId}")
                    .Small()
                    .Sends(new SettingsDisconnectConnection(conn.ConnectorId, conn.ConnectionId))
            ];
        }).ToArray();

        var oauthOffers = offers
            .Where(static o => !string.Equals(o.Id, "github", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(o.Id, "muncly", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(o.Id, "mcp", StringComparison.OrdinalIgnoreCase))
            .ToArray();

        var oauthRows = oauthOffers.Select(offer =>
        {
            var existing = active.FirstOrDefault(c => string.Equals(c.ConnectorId, offer.Id, StringComparison.OrdinalIgnoreCase));
            var isConnected = existing?.Status == ConnectionStatus.Connected;
            return (Node)HStack(Space.Sm)[
                Text(offer.DisplayName),
                MutedText(string.Join(", ", offer.AuthMethods)),
                FlexSpacer(),
                StatusPill(isConnected ? StatusKind.Done : StatusKind.Idle, isConnected ? "Connected" : "Not connected"),
                PrimaryButton(isConnected ? "Reconnect" : "Connect OAuth", "plug")
                    .Key($"conn:oauth:{offer.Id}")
                    .Small()
                    .WithIntent(IntentKind.OpenWindow, $"/connections/login/{offer.Id}")
            ];
        }).ToArray();

        var connectedCount = active.Count(static c => c.Status == ConnectionStatus.Connected);

        return Surface(SurfaceKind.Elevated, "Connections", "Catalog-driven external accounts, GitHub, Muncly CRM, and authenticated MCP servers")
            .Style(OsMixStyles.Surface(SurfaceKind.Elevated))[
            VStack(Space.Md)[
                HStack(Space.Sm)[
                    StatusPill(connectedCount > 0 ? StatusKind.Done : StatusKind.Idle, $"{connectedCount} active connection(s)"),
                    StatusPill(StatusKind.Done, $"{offers.Length} connector(s) available"),
                    FlexSpacer(),
                    Button("Refresh", Icons.RefreshCw)
                        .Key("conn:refresh")
                        .Small()
                        .Sends(new SettingsRefreshConnections())
                ],
                Card("Active Connections", "Live connections projected from IConnectionCatalog and connected MCP tool servers")
                    .Style(OsMixStyles.Card())[
                    activeRows.Length > 0
                        ? VStack(Space.Sm, activeRows).ToNode()
                        : MutedText("No external connections yet. Connect a service below.").ToNode()
                ],
                Card("GitHub", "Sign in with GitHub (Device Flow) or connect a repository via Fine-Grained PAT + optional webhook secret")
                    .Style(OsMixStyles.Card())[
                    VStack(Space.Sm)[
                        HStack(Space.Sm)[
                            PrimaryButton("Sign in with GitHub (Device Flow)", "log-in")
                                .Key("settings:github:device-flow-start")
                                .Small()
                                .Sends(new SettingsStartDeviceFlow("github", "main")),
                            FlexSpacer(),
                            Button("Copy webhook URL", "copy")
                                .Key("settings:github:copy-webhook-url")
                                .Small()
                                .WithIntent(copyWebhookIntent.Kind, copyWebhookIntent.Arg),
                            Button("Copy gh webhook forward", "terminal")
                                .Key("settings:github:copy-gh-forward")
                                .Small()
                                .WithIntent(copyGhForwardIntent.Kind, copyGhForwardIntent.Arg)
                        ],
                        !string.IsNullOrWhiteSpace(state.DeviceFlowUserCode)
                            ? VStack(Space.Xs)[
                                HStack(Space.Sm)[
                                    StatusPill(StatusKind.NeedsYou, $"Code: {state.DeviceFlowUserCode}"),
                                    Button("Open github.com/login/device ↗", "external-link")
                                        .Key("settings:github:device-flow-open")
                                        .Small()
                                        .WithIntent(IntentKind.OpenWindow, state.DeviceFlowVerificationUri ?? "https://github.com/login/device"),
                                    PrimaryButton("Complete Sign-In", Icons.Check)
                                        .Key("settings:github:device-flow-complete")
                                        .Small()
                                        .Sends(new SettingsCompleteDeviceFlow(state.DeviceFlowId ?? ""))
                                ],
                                !string.IsNullOrWhiteSpace(state.DeviceFlowStatus)
                                    ? MutedText(state.DeviceFlowStatus!).ToNode()
                                    : null
                            ].ToNode()
                            : !string.IsNullOrWhiteSpace(state.DeviceFlowStatus)
                                ? MutedText(state.DeviceFlowStatus!).ToNode()
                                : null,
                        MutedText($"Webhook endpoint: {webhookUrl}"),
                        SettingsControls.GitHubPatForm(children, state)
                    ]
                ],
                Card("Muncly CRM", "Connect your Muncly CRM workspace and MCP tools on connection/mcp/muncly-crm")
                    .Style(OsMixStyles.Card())[
                    SettingsControls.MunclyConnectForm(children, state)
                ],
                oauthRows.Length > 0
                    ? Card("Cloud Services (OAuth)", "Authorize Google Drive, Gmail, Salesforce, and other catalog connectors")
                        .Style(OsMixStyles.Card())[
                        VStack(Space.Sm, oauthRows)
                    ].ToNode()
                    : null,
                Card("Add Authenticated MCP Server", "Connect any Streamable HTTP MCP server with an optional Bearer token sealed in your vault")
                    .Style(OsMixStyles.Card())[
                    SettingsControls.McpAddFormControl(children, state)
                ]
            ]
        ];
    }

    public static Node RenderFilesSection(SettingsAppState state)
    {
        var active = state.ActiveConnections ?? [];
        IReadOnlyList<string>[] mountRows =
        [
            ["IntoChat Storage", "neuron://local/local-cloud/main", "Connected"],
            ["Local Computer Root", "neuron://local/disk.files", "Connected"],
            .. active.Select(static (c, idx) => (IReadOnlyList<string>)[$"Connected Mount {idx + 1}", c.Key, c.Status.ToString()]),
        ];

        return Surface(SurfaceKind.Elevated, "Files", "Manage IntoChat cloud storage, connected mounts, and local disk roots")
            .Style(OsMixStyles.Surface(SurfaceKind.Elevated))[
            VStack(Space.Md)[
                HStack(Space.Sm)[
                    StatusPill(StatusKind.Done, $"{mountRows.Length} mounted root(s)")
                ],
                Table("Mounted file roots", ["Mount", "Address", "Status"], mountRows),
                HStack(Space.Sm)[
                    Button("Resolve file conflict sheet", "git-compare")
                        .Key("files:conflict-sheet")
                        .Small()
                        .Sends(new SettingsNavigate("files", "", "conflict"))
                ]
            ]
        ];
    }

    public static Node RenderKeysSection(SettingsAppState state, SettingsChildren children)
    {
        (string Provider, string Detail, string SecretName, string Connection, string ButtonId, string ButtonLabel)[] vaults =
        [
            ("Anthropic", "Used for Claude Sonnet & Opus models", "ANTHROPIC_API_KEY", "anthropic", "seal-anthropic-key", "Seal Anthropic key"),
            ("OpenAI", "Used for GPT-5 & reasoning models", "OPENAI_API_KEY", "openai", "seal-openai-key", "Seal OpenAI key"),
            ("Google Gemini", "Used for Gemini Pro, Flash, TTS & Live models", "GOOGLE_API_KEY", "google", "seal-google-key", "Seal Google key"),
            ("OpenRouter", "Used for DeepSeek, Qwen, GLM, Kimi & MiniMax models", "OPENROUTER_API_KEY", "openrouter", "seal-openrouter-key", "Seal OpenRouter key"),
            ("xAI", "Used for Grok models", "XAI_API_KEY", "xai", "seal-xai-key", "Seal xAI key"),
            ("Brave Search", "Web search for agents · ≈ $5 per 1 000 searches", "BRAVE_API_KEY", "brave", "seal-brave-key", "Seal Brave key"),
            ("Tavily", "Web search for agents · 1 000 free per month", "TAVILY_API_KEY", "tavily", "seal-tavily-key", "Seal Tavily key"),
        ];

        var cards = vaults.Select(v =>
        {
            var isConfigured = SettingsProviderProjection.IsSecretConfigured(state, v.SecretName, v.Connection);
            return (Node)VStack(Space.Xs)[
                HStack(Space.Sm)[
                    StatusPill(isConfigured ? StatusKind.Done : StatusKind.Idle, isConfigured ? "Configured" : "Not set"),
                    MutedText(v.SecretName)
                ],
                new SecretCardNode(
                    v.Provider,
                    $"{v.Detail} · {(isConfigured ? "Configured" : "Not set")}",
                    SavedIn: $"Secure Vault · {v.SecretName}",
                    Status: isConfigured ? "saved" : "pending")
            ];
        }).ToArray();

        return Surface(SurfaceKind.Elevated, "API keys", "Keys are sealed in your personal vault via SecretRef and never enter neuron state or journals")
            .Style(OsMixStyles.Surface(SurfaceKind.Elevated))[
            VStack(Space.Md)[
                VStack(Space.Sm, cards),
                SettingsControls.ApiKeysForm(children, state),
                HStack(Space.Sm)[
                    PrimaryButton("Open secure vault window", Icons.KeyRound)
                        .Key("keys:open-vault")
                        .Small()
                        .WithIntent(IntentKind.OpenWindow, "os://window/secure")
                ]
            ]
        ];
    }

    public static Node RenderVariablesSection(SettingsAppState state)
    {
        IReadOnlyList<string>[] rows =
        [
            ["DEFAULT_MODEL", state.DefaultModel, "brain"],
            ["STYLE_PRESET", state.StylePreset, "local"],
            ["TIME_ZONE", state.TimeZone, "local"],
            .. (state.Variables ?? []).Select(static variable => (IReadOnlyList<string>)
            [
                variable.Key,
                SettingsAppState.RedactSecretDisplay(variable.Key, variable.Value),
                "brain",
            ]),
        ];

        return Surface(SurfaceKind.Elevated, "Variables and secrets", "Key-value configuration scoped across local, brain, and system — secret values are always redacted")
            .Style(OsMixStyles.Surface(SurfaceKind.Elevated))[
            VStack(Space.Md)[
                StatusPill(StatusKind.Done, $"{rows.Length} scoped entries"),
                Table(
                    "Variables & secrets",
                    ["Key", "Value", "Scope"],
                    rows,
                    sourceNeuronUri: "neuron://local/os.settings/settings")
            ]
        ];
    }
}
