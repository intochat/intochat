using System.Globalization;
using DigitalBrain.UI;
using IntoChat.OS.Modules.AI.Models;
using IntoChat.OS.Modules.Devices;
using IntoChat.OS.Modules.Web;
using IntoChat.OS.UI;
using static IntoChat.OS.UI.Kit;

namespace IntoChat.OS.Apps.Settings;

/// <summary>
/// Bounded section renderer for AI Models, local runtimes, model roles,
/// local server subpage, and Web &amp; Tools (search + MCP) subpage.
/// </summary>
internal static class SettingsModelsAndWebView
{
    public static Node RenderModelsSection(SettingsAppState state, string? subPage, SettingsChildren children)
    {
        if (string.Equals(subPage, "web-tools", StringComparison.OrdinalIgnoreCase))
        {
            return RenderWebToolsSubPage(state, children);
        }

        if (string.Equals(subPage, "local-server", StringComparison.OrdinalIgnoreCase))
        {
            return SettingsViewRenderer.DetailSubPage(
                "AI / local-server",
                "Local Model Server · Ollama & LM Studio",
                "Ollama Local Server · Zero-telemetry local inference endpoint.",
                [
                    ["Server", "Ollama Local Server"],
                    ["Endpoint", state.LocalOllamaEndpoint],
                    ["Active local model", state.DefaultModel],
                    ["Reasoning effort", "Medium · Local execution"],
                ],
                "back-to-ai-models",
                "Back to AI");
        }

        var providers = state.Providers is { Length: > 0 }
            ? state.Providers
            : SettingsProviderProjection.DefaultProviders(state);
        var hardwareLine = state.Device is { Hardware: { } hw }
            ? SettingsProviderProjection.FormatHardwareLine(hw)
            : null;
        var providerChildren = new List<Node>(providers.Length + 1);
        if (!string.IsNullOrWhiteSpace(hardwareLine))
        {
            providerChildren.Add(MutedText(hardwareLine).ToNode());
        }
        foreach (var provider in providers)
        {
            providerChildren.Add(HStack(Space.Sm)[
                new TagPill(provider.Label, "neutral", provider.Icon),
                FlexSpacer(),
                StatusPill(
                    provider.Status.StartsWith("Ready", StringComparison.Ordinal) ? StatusKind.Done : StatusKind.Idle,
                    provider.Status)
            ]);
        }

        var installQueue = state.Device?.Queue ?? [];
        var installChildren = new List<Node>(installQueue.Length + 2);
        if (installQueue.Length > 0)
        {
            foreach (var install in installQueue)
            {
                var label = KnownModels.Find(install.Model.Value)?.Name ?? install.Model.Value;
                installChildren.Add(HStack(Space.Sm)[
                    Text(label).ToNode(),
                    MutedText(install.Runtime.Value).Mono().ToNode(),
                    FlexSpacer(),
                    StatusPill(InstallStatusKind(install.State), FormatInstallStatus(install))
                ]);
            }
        }
        else
        {
            installChildren.Add(MutedText("No model installs in progress.").ToNode());
        }
        installChildren.Add(SettingsControls.InstallModelForm(children, state));

        var rolesByKey = state.Roles.ToDictionary(static row => row.Role);
        var roleControls = Enum.GetValues<ModelRole>()
            .Select(role =>
            {
                var row = rolesByKey.TryGetValue(role, out var existing)
                    ? existing
                    : SettingsProviderProjection.DefaultRoleRow(role, state);
                var options = row.Choices
                    .Select(static opt =>
                    {
                        var label = !opt.Available && !string.IsNullOrWhiteSpace(opt.Reason)
                            ? $"{opt.Label} · {opt.Reason}"
                            : opt.Label;
                        return new ComboBoxOption(opt.Id, label);
                    })
                    .ToArray();
                return SettingsControls.ComboBox(
                    children,
                    state,
                    SettingsControls.RoleKey(role),
                    $"{role} — {RoleDescription(role)}",
                    options);
            })
            .ToArray();

        return Surface(SurfaceKind.Elevated, "AI", "Providers and default models for each AI role")
            .Style(OsMixStyles.Surface(SurfaceKind.Elevated))[
            VStack(Space.Md)[
                Card("Connected Client Compute", "Allow this paired device to advertise ICodeSandbox over its P-256 link for sovereign code execution. Defaults ON on Desktop and OFF on Mobile.")
                    .Style(OsMixStyles.Card())[
                    VStack(Space.Sm)[
                        HStack(Space.Sm)[
                            new TagPill(ICodeSandbox.CapabilityId, "neutral", "cpu"),
                            FlexSpacer(),
                            StatusPill(
                                state.AllowClientCodeSandbox ? StatusKind.Done : StatusKind.Idle,
                                state.AllowClientCodeSandbox ? "Enabled · ICodeSandbox advertised" : "Disabled · Local/cloud fallback")
                        ],
                        VStack(Space.Sm, [.. SettingsControls.Toggles.Where(toggle => toggle.Section == "models").Select(toggle => SettingsControls.ToggleSwitch(children, state, toggle))])
                    ]
                ],
                Card("Providers", "Local runtimes and cloud providers available to this brain")
                    .Style(OsMixStyles.Card())[
                    VStack(Space.Sm, [.. providerChildren])
                ],
                Card("Local installs", "Download progress and queue on this device")
                    .Style(OsMixStyles.Card())[
                    VStack(Space.Sm, [.. installChildren])
                ],
                Card("Roles", "Choose the model each capability uses across Ino and background neurons")
                    .Style(OsMixStyles.Card())[
                    VStack(Space.Sm, roleControls)
                ],
                Card("Inference & Voice", "Tune reasoning effort for reasoning-capable models and choose your spoken voice persona")
                    .Style(OsMixStyles.Card())[
                    VStack(Space.Sm)[
                        SettingsControls.ComboBox(children, state, SettingsControls.ReasoningEffort, "Reasoning effort",
                        [
                            new ComboBoxOption("default", "Default"),
                            new ComboBoxOption("none", "None"),
                            new ComboBoxOption("low", "Low"),
                            new ComboBoxOption("medium", "Medium"),
                            new ComboBoxOption("high", "High"),
                        ]),
                        SettingsControls.ComboBox(children, state, SettingsControls.VoicePersona, "Voice persona",
                        [
                            new ComboBoxOption("default", "Default"),
                            new ComboBoxOption("marin", "Marin (OpenAI)"),
                            new ComboBoxOption("cedar", "Cedar (OpenAI)"),
                            new ComboBoxOption("coral", "Coral (OpenAI)"),
                            new ComboBoxOption("sage", "Sage (OpenAI)"),
                            new ComboBoxOption("alloy", "Alloy (OpenAI)"),
                            new ComboBoxOption("verse", "Verse (OpenAI)"),
                            new ComboBoxOption("Kore", "Kore (Gemini)"),
                            new ComboBoxOption("Puck", "Puck (Gemini)"),
                            new ComboBoxOption("Aoede", "Aoede (Gemini)"),
                            new ComboBoxOption("Charon", "Charon (Gemini)"),
                            new ComboBoxOption("Fenrir", "Fenrir (Gemini)"),
                        ])
                    ]
                ],
                HStack(Space.Sm)[
                    PrimaryButton("Local server (Ollama)", "cpu")
                        .Key("models:local-server")
                        .Small()
                        .Sends(new SettingsNavigate("models", "local-server")),
                    Button("Web & tools", "globe")
                        .Key("models:web-tools")
                        .Small()
                        .Sends(new SettingsNavigate("models", "web-tools")),
                    Button("Configure endpoint sheet", Icons.Sliders)
                        .Key("models:endpoint-sheet")
                        .Small()
                        .Sends(new SettingsNavigate("models", "", "ollama-config")),
                    Button("API keys & vault", Icons.KeyRound)
                        .Key("models:open-keys")
                        .Small()
                        .Sends(new SettingsNavigate("keys"))
                ]
            ]
        ];
    }

    private static Node RenderWebToolsSubPage(SettingsAppState state, SettingsChildren children)
    {
        var selectedProvider = SettingsControls.ResolveWebSearchProvider(state);
        var braveConfigured = SettingsProviderProjection.IsSecretConfigured(state, "BRAVE_API_KEY", "brave");
        var tavilyConfigured = SettingsProviderProjection.IsSecretConfigured(state, "TAVILY_API_KEY", "tavily");

        var servers = (state.McpServers ?? [])
            .Where(static s => !string.IsNullOrWhiteSpace(s.Name))
            .GroupBy(static s => s.Name, StringComparer.OrdinalIgnoreCase)
            .Select(static g => g.First())
            .OrderBy(static s => s.Name, StringComparer.Ordinal)
            .ToArray();

        var mcpRows = servers
            .Select(static s => (IReadOnlyList<string>)
            [
                string.IsNullOrWhiteSpace(s.Title) ? s.Name : s.Title,
                s.Url,
                s.Connected ? "Connected" : "Disconnected",
                s.ToolCount.ToString(CultureInfo.InvariantCulture),
                s.Error ?? "",
            ])
            .ToArray();

        var mcpActionRows = servers
            .Select(static server => (Node)HStack(Space.Sm)[
                Text(string.IsNullOrWhiteSpace(server.Title) ? server.Name : server.Title),
                MutedText(server.Url),
                FlexSpacer(),
                StatusPill(
                    server.Connected
                        ? StatusKind.Done
                        : !string.IsNullOrWhiteSpace(server.Error)
                            ? StatusKind.Error
                            : StatusKind.Idle,
                    server.Connected
                        ? $"Connected · {server.ToolCount} tool(s)"
                        : !string.IsNullOrWhiteSpace(server.Error)
                            ? server.Error!
                            : "Disconnected"),
                DangerButton("Remove", Icons.Trash2)
                    .Key($"mcp:{server.Name}")
                    .Small()
                    .Sends(new SettingsMcpRemove(server.Name))
            ])
            .ToArray();

        return Surface(SurfaceKind.Elevated, "AI / Web & tools", "Web search provider, search API keys, and connected MCP tool servers")
            .Style(OsMixStyles.Surface(SurfaceKind.Elevated))[
            VStack(Space.Md)[
                Card("Web search", "Choose how Ino searches the web. External pages and MCP results are fenced as untrusted content.")
                    .Style(OsMixStyles.Card())[
                    VStack(Space.Sm)[
                        SettingsControls.ComboBox(children, state, SettingsControls.WebSearchProvider, "Web search provider",
                        [
                            new ComboBoxOption(nameof(SearchProvider.Off), "Off"),
                            new ComboBoxOption(nameof(SearchProvider.Searxng), "SearXNG — free"),
                            new ComboBoxOption(nameof(SearchProvider.Brave), "Brave — ≈ $5 / 1k"),
                            new ComboBoxOption(nameof(SearchProvider.Tavily), "Tavily — 1k free / month"),
                            new ComboBoxOption(nameof(SearchProvider.Native), "Chat model's own search — billed by the provider"),
                        ]),
                        string.Equals(selectedProvider, nameof(SearchProvider.Searxng), StringComparison.OrdinalIgnoreCase)
                            ? SettingsControls.SearxngUrlForm(children, state)
                            : null,
                        Divider("Search API keys"),
                        HStack(Space.Sm)[
                            Text("Brave Search"),
                            MutedText("BRAVE_API_KEY · ≈ $5 / 1k"),
                            FlexSpacer(),
                            StatusPill(braveConfigured ? StatusKind.Done : StatusKind.Idle, braveConfigured ? "Configured" : "Not set")
                        ],
                        HStack(Space.Sm)[
                            Text("Tavily"),
                            MutedText("TAVILY_API_KEY · 1k free / month"),
                            FlexSpacer(),
                            StatusPill(tavilyConfigured ? StatusKind.Done : StatusKind.Idle, tavilyConfigured ? "Configured" : "Not set")
                        ],
                        HStack(Space.Sm)[
                            Button("API keys & vault", Icons.KeyRound)
                                .Key("models:open-keys")
                                .Small()
                                .Sends(new SettingsNavigate("keys"))
                        ]
                    ]
                ],
                Card("MCP tool servers", "Streamable HTTP MCP servers whose tools reach Ino")
                    .Style(OsMixStyles.Card())[
                    VStack(Space.Sm)[
                        servers.Length > 0
                            ? Table("MCP servers", ["Title", "URL", "Status", "Tools", "Error"], mcpRows)
                            : MutedText("No MCP servers configured yet.").ToNode(),
                        mcpActionRows.Length > 0
                            ? VStack(Space.Xs, mcpActionRows).ToNode()
                            : null,
                        Divider("Add MCP server"),
                        SettingsControls.McpAddFormControl(children, state)
                    ]
                ],
                HStack(Space.Sm)[
                    Button("Back to AI", "arrow-left")
                        .Key("back-to-ai-web-tools")
                        .Small()
                        .Sends(new SettingsGoBack())
                ]
            ]
        ];
    }

    private static string FormatInstallStatus(Install install)
    {
        var pct = (int)Math.Floor(Math.Clamp(install.Percent, 0d, 100d));
        return install.State switch
        {
            InstallState.Queued => "Queued",
            InstallState.Downloading => string.Create(CultureInfo.InvariantCulture, $"Downloading {pct}%"),
            InstallState.Verifying => "Verifying",
            InstallState.Ready => "Ready",
            InstallState.Loaded => "Loaded",
            InstallState.Failed => string.IsNullOrWhiteSpace(install.Error) ? "Failed" : $"Failed · {install.Error}",
            InstallState.Cancelled => "Cancelled",
            _ => install.State.ToString(),
        };
    }

    private static StatusKind InstallStatusKind(InstallState state) => state switch
    {
        InstallState.Ready or InstallState.Loaded => StatusKind.Done,
        InstallState.Queued or InstallState.Downloading or InstallState.Verifying => StatusKind.Working,
        InstallState.Failed => StatusKind.Error,
        _ => StatusKind.Idle,
    };

    private static string RoleDescription(ModelRole role) => role switch
    {
        ModelRole.Chat => "Default conversation",
        ModelRole.Think => "Hard tasks, Engineer, planning",
        ModelRole.Fast => "Titles, routing, System One",
        ModelRole.Hear => "Speech → text",
        ModelRole.Speak => "Text → speech",
        ModelRole.Talk => "Realtime voice-to-voice",
        ModelRole.Embed => "Memory recall",
        ModelRole.Image => "Image generation",
        _ => role.ToString(),
    };
}
