using DigitalBrain.UI;

namespace IntoChat.OS.Modules.Apps;

internal static class InoViewRenderer
{
    private static readonly (string Id, string Title, string Group, string Icon, string? Badge)[] ArcSessions =
    [
        ("q4-renewal", "Nordlys Q4 Renewal", "Pinned", "sparkles", "Needs you"),
        ("morning-brief", "Morning Brief", "Pinned", "sun", "3"),
        ("invoice-check", "Tripletex Invoice Check", "Today", "key-round", "Vault"),
        ("first-run", "Ino Setup · First Run", "Today", "compass", null),
    ];

    public static View RenderSide(InoAppState state) => new(
        "Chats",
        new Column(
        [
            new Surface(
                [
                    new Row(
                    [
                        new TagPill("Ino · Arc Sidebar", "accent", "sparkles"),
                        new TagPill($"{state.Turns.Length} turn(s)", "neutral"),
                    ]) { Gap = 6 },
                    new Text(state.Status) { Tone = "muted", Tabular = true },
                    new Button("+ New Session", new ButtonAction("apps/ino", "new", []), Tone: ButtonTone.Accent) { Small = true, Icon = "plus" },
                ],
                Kind: SurfaceKind.Acrylic,
                Title: state.Title),
            new Divider("PINNED SESSIONS"),
            .. ArcSessions.Where(s => s.Group == "Pinned").Select(s => (Node)Ui.NavItem(
                $"arc-{s.Id}",
                s.Title,
                s.Icon,
                $"send:/scenario {s.Id}",
                selected: state.ConversationId == s.Id,
                badge: s.Badge)),
            new Divider("TODAY"),
            .. ArcSessions.Where(s => s.Group == "Today").Select(s => (Node)Ui.NavItem(
                $"arc-{s.Id}",
                s.Title,
                s.Icon,
                $"send:/scenario {s.Id}",
                selected: state.ConversationId == s.Id,
                badge: s.Badge)),
            new Collection([new Entry(state.ConversationId, state.Title, $"{state.Turns.Length} turn(s)")]),
        ])
        { Gap = 8 });

    public static View Render(InoAppState state)
    {
        var waitingCard = state.ActiveCards.FirstOrDefault(c => c.IsWaiting);
        return new(
            "Ino",
            new Column(
            [
                new Surface(
                    [
                        new Row(
                        [
                            new TagPill("Ino Assistant", "accent", "bot"),
                            new TagPill(state.VoiceStatus == "recording" ? "Listening" : "Ready", state.VoiceStatus == "recording" ? "working" : "ok"),
                            .. state.ConnectedDatabase is { Length: > 0 } db ? new Node[] { new TagPill(db, "info", "database") } : [],
                            .. waitingCard is not null ? new Node[] { new TagPill("Waiting on you · Docked below", "warn", "hand") } : [],
                        ]) { Gap = 8 },
                        new Text(state.Status) { Tabular = true },
                    ],
                    Kind: SurfaceKind.Acrylic,
                    Title: state.Title),
                .. state.ActiveCards.SelectMany(RenderCardNodes),
                .. waitingCard is not null ? new Node[] { RenderDockedPromptBanner(waitingCard) } : [],
                .. state.Turns.Select((turn, i) => (Node)new Surface(
                    [
                        new Row(
                        [
                            new TagPill($"Turn {i + 1}", "neutral"),
                            new TagPill(turn.RouteHint, "accent"),
                            .. turn.RecalledCount > 0 ? new Node[] { new TagPill($"{turn.RecalledCount} memory hit(s)", "ok", "brain") } : [],
                        ]) { Gap = 6 },
                        new Heading(turn.UserMessage, Level: 3),
                        new Text(turn.Reply),
                    ],
                    Kind: SurfaceKind.Elevated,
                    Title: turn.UserMessage,
                    Subtitle: turn.RouteHint)),
            ])
            { Gap = 12 });
    }

    public static InoAppState BuildAppInstallLinkState(InoAppState currentState, string text)
    {
        var cardId = $"install-{currentState.ActiveCards.Length + 1}";
        var card = new InoCard(
            cardId,
            "app-install",
            "Tripletex Accounting Sync (v1.4.0)",
            "Verified Smart App from Nordlys Digital · Requests read access to Files & Postgres.",
            "neuron://local/apps/tripletex-sync",
            "pending",
            $"neuron://local/apps/ino#{cardId}");
        var turns = currentState.Turns.Append(new InoTurn(
            text,
            "Detected a Smart App install link (`nordlys/tripletex-sync`). Review its manifest and capability permissions below before installing.",
            "store:install-link",
            0)).ToArray();
        return currentState with
        {
            Title = "Install Tripletex Accounting Sync",
            Status = "Waiting for Smart App capability review",
            Turns = turns,
            Cards = [.. currentState.ActiveCards, card],
        };
    }

    public static InoAppState BuildScenarioSessionState(string scenarioId, UserSettingsState settings) => scenarioId switch
    {
        "morning-brief" => new InoAppState(
            "morning-brief",
            "Morning Brief",
            $"Scenario: Morning Brief ({settings.DefaultModel})",
            "idle",
            [
                new InoTurn(
                    "Prepare my Morning Brief for Nordlys AS",
                    "Good morning! You have **3 meetings**, **2 Q4 renewals** (€426,000 pipeline), and **1 overdue invoice** in Tripletex.",
                    "scenario:morning-brief",
                    2),
            ],
            [
                new InoCard(
                    "choice-brief",
                    "choice",
                    "Which Nordlys workspace should receive the Q4 renewal brief?",
                    "Select a target Circle (1..2) or press Enter.",
                    "neuron://local/circles/nordlys",
                    "pending",
                    "neuron://local/apps/ino#choice-brief"),
            ]),
        "invoice-check" => new InoAppState(
            "invoice-check",
            "Tripletex Invoice Check",
            "Waiting on Secure Vault credential",
            "idle",
            [
                new InoTurn(
                    "Check overdue Q4 invoices in Tripletex",
                    "I need your **Tripletex Session Token** to read the Q4 ledger. Enter it in the **Secure Vault** window so it never touches the LLM.",
                    "vault:secret",
                    0),
            ],
            [
                new InoCard(
                    "secret-tripletex",
                    "secret",
                    "Tripletex Session Token",
                    "Zero-leakage credential request · Encrypted in neuron://local/vault/secrets",
                    "neuron://local/vault/secrets#TRIPLETEX_SESSION_TOKEN",
                    "pending",
                    "neuron://local/apps/ino#secret-tripletex"),
            ]),
        "first-run" => new InoAppState(
            "first-run",
            "Ino Setup · First Run",
            "Guided onboarding · 3 steps",
            "idle",
            [
                new InoTurn(
                    "/scenario first-run",
                    "Welcome to **IntoChat OS**! Let's verify your default AI model (`" + settings.DefaultModel + "`), connect your **Files** vault (`neuron://local/storage.files`), and seal API keys in **Secure**.",
                    "onboarding:setup",
                    0),
            ],
            [
                new InoCard(
                    "setup-record",
                    "record",
                    "IntoChat OS First-Run Checklist",
                    "All 3 local brain capabilities online",
                    "neuron://local/os.settings/settings",
                    "ready",
                    "neuron://local/apps/ino#setup-record"),
            ]),
        _ => new InoAppState(
            "q4-renewal",
            "Nordlys Q4 Renewal",
            "Waiting for approval · CloseDate -> 15 Nov 2026",
            "idle",
            [
                new InoTurn(
                    "Check Kari Holm's email and update Nordlys Q4 close date to 15 Nov 2026",
                    "Kari Holm confirmed moving the **Nordlys Q4 Enterprise** renewal (`480,000 NOK`) to **15 Nov 2026**. Please approve the Salesforce update below.",
                    $"agent:{settings.DefaultModel}",
                    1),
            ],
            [
                new InoCard(
                    "card-1",
                    "approval",
                    "Update close date on Nordlys Q4 renewal to 15 Nov 2026?",
                    "Ino matched Kari Holm's email to your open Salesforce opportunity (480,000 NOK).",
                    "neuron://local/os.confirmations/inbox",
                    "pending",
                    "neuron://local/apps/ino#card-1"),
                new InoCard(
                    "draft-1",
                    "draft",
                    "Nordlys Q4 Enterprise Renewal — 15 Nov confirmation",
                    "Hi Kari, Confirmed: we updated the Q4 renewal close date to 15 Nov 2026 (480,000 NOK).",
                    "neuron://local/apps/ino#draft-1",
                    "pending",
                    "neuron://local/apps/ino#draft-1"),
            ]),
    };

    private static Node RenderDockedPromptBanner(InoCard card) => new Surface(
        [
            new Row(
            [
                new TagPill("Docked Waiting Card", "warn", "keyboard"),
                new Text($"Waiting on you · Docked below · Keyboard: ↵ Approve/Confirm · Esc Deny/Skip · {card.Title}") { Tone = "strong", Tabular = true },
                new Button("Confirm (↵)", new ButtonAction("apps/ino", "approve-card", [card.Id, "true"]), Tone: ButtonTone.Accent) { Small = true, Icon = "check" },
                new Button("Dismiss (Esc)", new ButtonAction("apps/ino", "approve-card", [card.Id, "false"])) { Small = true, Style = "ghost" },
            ]) { Gap = 8 },
        ],
        Kind: SurfaceKind.Elevated,
        Title: $"Waiting on you: {card.Title}");

    private static IEnumerable<Node> RenderCardNodes(InoCard card)
    {
        yield return new Text($"[{card.Kind}:{card.Status}] {card.Title} ({card.SourceNeuronUri} | scope:{card.EffectiveScopeUri})") { Tone = "muted", Mono = true, Tabular = true };
        if (card.Kind == "approval")
        {
            yield return new ApprovalCardNode(
                card.Id,
                card.Title,
                $"{card.Summary} (scope:{card.EffectiveScopeUri})",
                [
                    new ApprovalRowNode("Status", "pending", card.Status),
                    new ApprovalRowNode("Source Neuron", "local", card.SourceNeuronUri),
                    new ApprovalRowNode("Scope URI", "ino", card.EffectiveScopeUri),
                ],
                SystemName: "Ino",
                Status: card.Status,
                OnApprove: card.Status == "pending" ? new ButtonAction("apps/ino", "approve-card", [card.Id, "true"]) : null,
                OnReject: card.Status == "pending" ? new ButtonAction("apps/ino", "approve-card", [card.Id, "false"]) : null,
                SourceNeuronUri: card.SourceNeuronUri);
        }
        else if (card.Kind == "choice")
        {
            yield return new ChoiceCardNode(
                card.Id,
                card.Title,
                [
                    new ChoiceOptionNode("Nordlys Enterprise (Primary)", "neuron://local/circles/enterprise", new ButtonAction("apps/ino", "approve-card", [card.Id, "true"])),
                    new ChoiceOptionNode("Executive Board Circle", "neuron://local/circles/board", new ButtonAction("apps/ino", "approve-card", [card.Id, "true"])),
                ],
                PickedIndex: card.Status == "approved" ? 0 : null);
        }
        else if (card.Kind == "draft")
        {
            yield return new DraftCardNode(
                card.Id,
                "email",
                "Astrid Lindqvist <astrid@nordlys.no>",
                "Kari Holm <kari@nordlys.no>",
                card.Summary,
                Subject: card.Title,
                Status: card.Status == "approved" ? "sent" : card.Status,
                OnSend: card.Status == "pending" ? new ButtonAction("apps/ino", "approve-card", [card.Id, "true"]) : null,
                OnDiscard: card.Status == "pending" ? new ButtonAction("apps/ino", "approve-card", [card.Id, "false"]) : null);
        }
        else if (card.Kind == "secret")
        {
            yield return new SecretCardNode(
                card.Title,
                card.Summary,
                SavedIn: "Secure Vault · neuron://local/vault/secrets",
                Status: card.Status == "approved" ? "saved" : card.Status,
                OnEnter: new ButtonAction("os://window/secure", "os://window/secure", []),
                OnSkip: new ButtonAction("apps/ino", "approve-card", [card.Id, "false"]));
        }
        else if (card.Kind == "app-install")
        {
            yield return new RecordCardNode(
                "Smart App Manifest",
                card.Title,
                [
                    new RecordFieldNode("Publisher", "Nordlys Digital (Verified)"),
                    new RecordFieldNode("Capabilities", "Files (read), Postgres (read)"),
                    new RecordFieldNode("Status", card.Status),
                ],
                Subtitle: card.Summary,
                LinkLabel: "Review & Install",
                OnOpen: new ButtonAction("os://sheet/install", "os://sheet/install", []));
        }
        else if (card.Kind == "table" && card.Columns is { Length: > 0 } cols && card.Rows is { Length: > 0 } rows)
        {
            yield return new DataTable(
                card.Title,
                cols,
                rows.Select(r => (IReadOnlyList<string>)r).ToArray(),
                SourceNeuronUri: card.SourceNeuronUri,
                From: "Postgres");
        }
        else if (card.Kind == "chart" && card.ChartPoints is { Length: > 0 } points)
        {
            yield return new Chart(
                card.ChartKind ?? "bar",
                card.Title,
                points.Select(pt => new ChartPointNode(pt.Label, pt.Value)).ToArray(),
                SeriesName: "Current",
                SourceNeuronUri: card.SourceNeuronUri,
                From: "Postgres",
                Prefix: "€");
        }
        else if (card.Kind is "photocraft" or "filmcraft")
        {
            var targetApp = card.Kind;
            var label = targetApp == "photocraft" ? "Open PhotoCraft Studio" : "Open FilmCraft Studio";
            yield return new RecordCardNode(
                targetApp == "photocraft" ? "PhotoCraft · Image Studio" : "FilmCraft · Video Studio",
                card.Title,
                [
                    new RecordFieldNode("Status", card.Status),
                    new RecordFieldNode("Studio Neuron", card.SourceNeuronUri),
                    new RecordFieldNode("Scope", card.EffectiveScopeUri),
                ],
                Subtitle: card.Summary,
                LinkLabel: label,
                OnOpen: new ButtonAction($"apps/{targetApp}", "open", []));
            yield return new WebBrowser(targetApp == "photocraft" ? "http://localhost:18082" : "http://localhost:18083", card.Title, $"{card.Id}-studio", 420, "wasm-ui");
        }
        else
        {
            yield return new RecordCardNode(
                card.Kind,
                card.Title,
                [
                    new RecordFieldNode("Status", card.Status),
                    new RecordFieldNode("Neuron URI", card.SourceNeuronUri),
                    new RecordFieldNode("Scope", card.EffectiveScopeUri),
                ],
                Subtitle: card.Summary,
                LinkLabel: card.Kind == "file" ? "Open in Files" : "Open Neuron",
                OnOpen: card.Kind == "file" ? new ButtonAction("apps/files", "open", []) : null);
        }
    }
}
