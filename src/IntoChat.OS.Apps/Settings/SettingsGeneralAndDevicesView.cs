using DigitalBrain.Capabilities.Connections;
using DigitalBrain.UI;
using IntoChat.OS.Modules.Devices;
using IntoChat.OS.Modules.Devices.Link;
using IntoChat.OS.UI;
using static IntoChat.OS.UI.Kit;

namespace IntoChat.OS.Apps.Settings;

/// <summary>
/// Bounded section renderer for Preferences (General, Desktop &amp; Dock) and
/// Devices / Cloud Pairing subpages.
/// </summary>
internal static class SettingsGeneralAndDevicesView
{
    public static Node RenderGeneralSection(SettingsAppState state, string? subPage, SettingsChildren children)
    {
        if (string.Equals(subPage, "devices", StringComparison.OrdinalIgnoreCase))
        {
            return VStack(Space.Md)[
                state.IsCloudTopology
                    ? RenderCloudConnectedDevicesCard(state)
                    : RenderDesktopCloudPairingCard(state, children),
                HStack(Space.Sm)[
                    Button("Back to General", "arrow-left")
                        .Key("settings:devices:back-general")
                        .Small()
                        .Sends(new SettingsNavigate("general"))
                ]
            ];
        }

        var connectionCount = (state.ActiveConnections ?? []).Count(static c => c.Status == ConnectionStatus.Connected);

        return VStack(Space.Md)[
            Grid(3, Space.Md)[
                StatCard("stat-preset", "Active UI identity", state.StylePreset, state.BarLayout, BadgeTone.Accent, "palette"),
                StatCard("stat-model", "Default brain model", state.DefaultModel, "Local + cloud", BadgeTone.Done, "cpu"),
                StatCard("stat-vault", "Connected integrations", $"{connectionCount}", connectionCount > 0 ? "Active" : "None yet", BadgeTone.Neutral, Icons.KeyRound)
            ],
            Surface(SurfaceKind.Elevated, "Appearance & Regional Preferences", "How IntoChat looks and formats dates for you")
                .Style(OsMixStyles.Surface(SurfaceKind.Elevated))[
                VStack(Space.Md)[
                    SettingsControls.ComboBox(children, state, SettingsControls.ThemeMode, "Appearance mode",
                    [
                        new ComboBoxOption("light", "Light"),
                        new ComboBoxOption("dark", "Dark"),
                        new ComboBoxOption("auto", "Auto (follow system)"),
                    ]),
                    HStack(Space.Sm)[
                        SettingsViewRenderer.PrefButton(state, "pref-fluent-acrylic", "Fluent Acrylic", state.StylePreset == "fluent-acrylic", preset: "fluent-acrylic"),
                        SettingsViewRenderer.PrefButton(state, "pref-spatial-glass", "Spatial Glass", state.StylePreset == "spatial-glass", preset: "spatial-glass"),
                        SettingsViewRenderer.PrefButton(state, "pref-cyber-synaptic", "Cyber Synaptic", state.StylePreset == "cyber-synaptic", preset: "cyber-synaptic"),
                        SettingsViewRenderer.PrefButton(state, "pref-warm-paper", "Warm Paper", state.StylePreset == "warm-paper", preset: "warm-paper"),
                        SettingsViewRenderer.PrefButton(state, "pref-midnight", "Midnight", state.StylePreset == "midnight", preset: "midnight"),
                        SettingsViewRenderer.PrefButton(state, "pref-nordic", "Nordic", state.StylePreset == "nordic", preset: "nordic")
                    ],
                    SettingsControls.RegionalForm(children, state),
                    Table(
                        "Regional settings",
                        ["Setting", "Active value"],
                        [
                            ["Display name", state.DisplayName],
                            ["Language", state.Language],
                            ["Time zone", state.TimeZone],
                            ["Date format", state.DateFormat],
                            ["Desktop tone", state.DesktopTone],
                        ])
                ]
            ],
            Card("Profile, Secure Vault & OS Sheets", "Open focused security windows without leaving your current workspace")
                .Style(OsMixStyles.Card())[
                HStack(Space.Sm)[
                    PrimaryButton("Open profile & 2FA", "user")
                        .Key("intent:profile")
                        .Small()
                        .WithIntent(IntentKind.OpenWindow, "os://window/profile"),
                    Button("Open secure vault", Icons.KeyRound)
                        .Key("intent:vault")
                        .Small()
                        .WithIntent(IntentKind.OpenWindow, "os://window/secure"),
                    Button("Install app by link", "package-plus")
                        .Key("intent:install")
                        .Small()
                        .WithIntent(IntentKind.OpenWindow, "os://sheet/install"),
                    Button("Resolve file conflict", "git-compare")
                        .Key("intent:conflict")
                        .Small()
                        .WithIntent(IntentKind.OpenWindow, "os://sheet/conflict")
                ]
            ]
        ];
    }

    public static Node RenderDesktopSection(SettingsAppState state, SettingsChildren children) =>
        Surface(SurfaceKind.Elevated, "Desktop & Dock", "Ambient canvas tone, adaptive bar layout, and status indicators")
            .Style(OsMixStyles.Surface(SurfaceKind.Elevated))[
            VStack(Space.Md)[
                Heading("Desktop canvas tone", 3, "Choose the warm paper or deep ink surface behind windows."),
                HStack(Space.Sm)[
                    SettingsViewRenderer.PrefButton(state, "tone-cream", "Cream", state.DesktopTone == "cream", themeMode: state.ThemeMode, desktopTone: "cream"),
                    SettingsViewRenderer.PrefButton(state, "tone-sand", "Sand", state.DesktopTone == "sand", themeMode: state.ThemeMode, desktopTone: "sand"),
                    SettingsViewRenderer.PrefButton(state, "tone-peat", "Peat", state.DesktopTone == "peat", themeMode: state.ThemeMode, desktopTone: "peat"),
                    SettingsViewRenderer.PrefButton(state, "tone-ink", "Ink", state.DesktopTone == "ink", themeMode: state.ThemeMode, desktopTone: "ink")
                ],
                SettingsControls.ComboBox(children, state, SettingsControls.BarLayout, "OS bar layout",
                [
                    new ComboBoxOption("floating-glass", "Floating Glass"),
                    new ComboBoxOption("compact-pill", "Compact Pill"),
                    new ComboBoxOption("classic-bottom", "Classic Bottom"),
                ]),
                Divider("OS bar indicators"),
                VStack(Space.Sm, [.. SettingsControls.Toggles.Where(toggle => toggle.Section == "desktop").Select(toggle => SettingsControls.ToggleSwitch(children, state, toggle))])
            ]
        ];

    private static Node RenderDesktopCloudPairingCard(SettingsAppState state, SettingsChildren children)
    {
        var linked = state.DeviceLinkState == LinkState.Connected;
        var statusLabel = state.DeviceLinkState switch
        {
            LinkState.Connected => string.IsNullOrWhiteSpace(state.DeviceLinkCircleId)
                ? $"Linked to {state.DeviceLinkServer ?? state.CloudLinkUrl}"
                : $"Linked to {state.DeviceLinkServer ?? state.CloudLinkUrl}  ·  Circle {state.DeviceLinkCircleId}",
            LinkState.Offline => $"Offline  ·  {state.DeviceLinkServer ?? state.CloudLinkUrl}",
            LinkState.Revoked => "Revoked  ·  Link this PC again",
            _ => "Not linked",
        };
        var statusKind = state.DeviceLinkState switch
        {
            LinkState.Connected => StatusKind.Done,
            LinkState.Offline => StatusKind.Working,
            LinkState.Revoked => StatusKind.Error,
            _ => StatusKind.Idle,
        };

        var browserLinkUrl = DeviceEnrollUri.BuildBrowserLinkUrl(
            Uri.TryCreate(state.CloudLinkUrl, UriKind.Absolute, out var parsedCloud) ? parsedCloud : null).AbsoluteUri;
        var capabilityPills = (state.DeviceLinkCapabilities is { Length: > 0 } caps ? caps : ["None advertised"])
            .Select(cap => (Node)new TagPill(FormatCapabilityLabel(cap), "neutral", IconForCapability(cap)))
            .ToArray();

        var sandboxToggle = SettingsControls.Toggles.First(t => t.Key == SettingsControls.AllowClientCodeSandbox);
        var foldersToggle = SettingsControls.Toggles.First(t => t.Key == SettingsControls.AllowWindowsFolderGateway);
        var consoleToggle = SettingsControls.Toggles.First(t => t.Key == SettingsControls.AllowConsole);
        var engineeringToggle = SettingsControls.Toggles.First(t => t.Key == SettingsControls.AllowConnectedEngineering);

        return Card("Connect this PC to IntoChat Cloud", "Link this Windows PC to your IntoChat Cloud brain for sandboxed code execution and consented PC access.")
            .Style(OsMixStyles.Card())[
            VStack(Space.Md)[
                HStack(Space.Sm)[
                    StatusPill(statusKind, statusLabel),
                    state.DeviceLinkLastContactedAt is { } lastSeen
                        ? MutedText($"Last heartbeat  ·  {lastSeen.UtcDateTime:yyyy-MM-dd HH:mm:ss} UTC").Tabular().ToNode()
                        : null
                ],
                HStack(Space.Sm, capabilityPills),
                !string.IsNullOrWhiteSpace(state.PendingEnrollmentServerOrigin)
                    ? Card("Confirm cloud link", "A browser handoff requested linking this PC. Verify the server address before confirming.")
                        .Style(OsMixStyles.Card(elevated: true))[
                        VStack(Space.Sm)[
                            HStack(Space.Sm)[
                                new TagPill(state.PendingEnrollmentServerOrigin!, "accent", "link"),
                                state.PendingEnrollmentExpiresAt is { } pendingExp
                                    ? MutedText($"Expires {pendingExp.UtcDateTime:HH:mm} UTC").Tabular().ToNode()
                                    : null
                            ],
                            HStack(Space.Sm)[
                                PrimaryButton("Link this PC", "link")
                                    .Key("settings:devices:confirm-pending")
                                    .Small()
                                    .Sends(new SettingsConfirmPendingEnrollment()),
                                Button("Cancel", "x")
                                    .Key("settings:devices:cancel-pending")
                                    .Small()
                                    .Sends(new SettingsCancelPendingEnrollment())
                            ]
                        ]
                    ].ToNode()
                    : null,
                !string.IsNullOrWhiteSpace(state.DevicePairingFeedback)
                    ? Banner(
                        state.DevicePairingFeedbackIsError ? "Pairing notice" : "Device pairing",
                        state.DevicePairingFeedback!,
                        tone: state.DevicePairingFeedbackIsError ? "error" : "info")
                    : null,
                HStack(Space.Sm)[
                    PrimaryButton("Connect in browser", "external-link")
                        .Key("settings:devices:connect-browser")
                        .Small()
                        .Sends(new SettingsLaunchBrowserPairing(state.CloudLinkUrl)),
                    Button("Copy browser link", "copy")
                        .Key("settings:devices:copy-browser-link")
                        .Small()
                        .WithIntent(IntentKind.Copy, browserLinkUrl),
                    Button(state.ShowManualPairingForm ? "Hide pairing code" : "Enter pairing code", Icons.KeyRound)
                        .Key("settings:devices:toggle-manual-code")
                        .Small()
                        .Sends(new SettingsToggleManualPairing()),
                    linked
                        ? DangerButton("Disconnect PC", "log-out")
                            .Key("settings:devices:disconnect")
                            .Small()
                            .Sends(new SettingsDisconnectDevice())
                            .ToNode()
                        : null
                ],
                state.ShowManualPairingForm
                    ? SettingsControls.PairDeviceForm(children, state)
                    : null,
                Divider("PC resource access"),
                VStack(Space.Sm)[
                    SettingsControls.ToggleSwitch(children, state, sandboxToggle),
                    SettingsControls.ToggleSwitch(children, state, foldersToggle),
                    HStack(Space.Sm)[
                        SettingsControls.ToggleSwitch(children, state, consoleToggle),
                        new TagPill("Person only", "accent", "shield-check")
                    ],
                    HStack(Space.Sm)[
                        SettingsControls.ToggleSwitch(children, state, engineeringToggle),
                        new TagPill("Desktop only", "accent", "git-branch")
                    ]
                ]
            ]
        ];
    }

    private static Node RenderCloudConnectedDevicesCard(SettingsAppState state)
    {
        var devices = state.EnrolledDevices ?? [];
        var rows = new List<Node>();
        foreach (var device in devices)
        {
            var caps = device.AdvertisedCapabilities is { Length: > 0 }
                ? device.AdvertisedCapabilities
                : [];
            var rowItems = new List<Node?>
            {
                Text(device.DeviceName).ToNode(),
                StatusPill(
                    device.Revoked ? StatusKind.Error : device.Online ? StatusKind.Done : StatusKind.Idle,
                    device.Revoked ? "Revoked" : device.Online ? "Online" : "Offline"),
            };
            foreach (var cap in caps)
            {
                rowItems.Add(new TagPill(FormatCapabilityLabel(cap), "neutral", IconForCapability(cap)));
            }
            if (device.LastHeartbeatAt is { } hb)
            {
                rowItems.Add(MutedText($"Last seen  ·  {hb.UtcDateTime:HH:mm:ss} UTC").Tabular().ToNode());
            }
            rowItems.Add(FlexSpacer());
            if (!device.Revoked)
            {
                rowItems.Add(DangerButton("Revoke", Icons.Trash2)
                    .Key($"settings:devices:revoke:{device.MachineId}")
                    .Small()
                    .Sends(new SettingsRevokeEnrolledDevice(device.MachineId))
                    .ToNode());
            }

            rows.Add(HStack(Space.Sm, rowItems.ToArray()));
        }

        return Card("Connected devices", "Manage paired Windows PCs enrolled in this cloud brain and inspect their live capabilities.")
            .Style(OsMixStyles.Card())[
            VStack(Space.Md)[
                !string.IsNullOrWhiteSpace(state.DevicePairingFeedback)
                    ? Banner(
                        state.DevicePairingFeedbackIsError ? "Enrollment notice" : "Connected devices",
                        state.DevicePairingFeedback!,
                        tone: state.DevicePairingFeedbackIsError ? "error" : "info")
                    : null,
                HStack(Space.Sm)[
                    PrimaryButton("Link new PC", Icons.Plus)
                        .Key("settings:devices:mint-code")
                        .Small()
                        .Sends(new SettingsMintEnrollmentCode(state.CloudLinkUrl)),
                    !string.IsNullOrWhiteSpace(state.MintedEnrollmentCode)
                        ? Button("Copy code", "copy")
                            .Key("settings:devices:copy-code")
                            .Small()
                            .WithIntent(IntentKind.Copy, state.MintedEnrollmentCode!)
                            .ToNode()
                        : null,
                    !string.IsNullOrWhiteSpace(state.MintedEnrollmentHandoffUri)
                        ? Button("Copy handoff link", "copy")
                            .Key("settings:devices:copy-handoff")
                            .Small()
                            .WithIntent(IntentKind.Copy, state.MintedEnrollmentHandoffUri!)
                            .ToNode()
                        : null
                ],
                !string.IsNullOrWhiteSpace(state.MintedEnrollmentCode)
                    ? Card("New pairing code", state.MintedEnrollmentExpiresAt is { } exp ? $"Expires {exp.UtcDateTime:yyyy-MM-dd HH:mm} UTC" : "Short-lived single-use code")
                        .Style(OsMixStyles.Card(elevated: true))[
                        VStack(Space.Xs)[
                            Text($"Code  ·  {state.MintedEnrollmentCode}").Tabular(),
                            !string.IsNullOrWhiteSpace(state.MintedEnrollmentHandoffUri)
                                ? MutedText($"Handoff link  ·  {state.MintedEnrollmentHandoffUri}").Tabular().ToNode()
                                : null
                        ]
                    ].ToNode()
                    : null,
                devices.Length == 0
                    ? Empty(
                        "No connected PCs yet",
                        "Link your Windows PC to run code and access shared folders from this cloud brain.")
                    : VStack(Space.Sm, rows.ToArray())
            ]
        ];
    }

    private static string FormatCapabilityLabel(string capability) => capability switch
    {
        ClientComputeConsentPolicy.CodeSandboxCapability => "Run code on this PC",
        ClientComputeConsentPolicy.WindowsFolderGatewayCapability => "Shared folders",
        ClientComputeConsentPolicy.ConsoleCapability => "Interactive shell",
        ClientComputeConsentPolicy.ConnectedEngineeringCapability => "Connected engineering",
        _ => capability,
    };

    private static string IconForCapability(string capability) => capability switch
    {
        ClientComputeConsentPolicy.CodeSandboxCapability => "cpu",
        ClientComputeConsentPolicy.WindowsFolderGatewayCapability => "folder-check",
        ClientComputeConsentPolicy.ConsoleCapability => "terminal",
        ClientComputeConsentPolicy.ConnectedEngineeringCapability => "git-branch",
        _ => "monitor",
    };
}
