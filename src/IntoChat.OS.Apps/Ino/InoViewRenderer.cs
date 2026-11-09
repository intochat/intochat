using DigitalBrain.UI;
using IntoChat.OS.Apps.Markdraw;
using IntoChat.OS.Modules.AI.Inference;
using IntoChat.OS.Modules.Apps;
using IntoChat.OS.Modules.Apps.Approvals;
using IntoChat.OS.Modules.Flutter.Chat;

namespace IntoChat.OS.Apps.Ino;

internal static class InoViewRenderer
{
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
                    new Button("new-session", "+ New Session", Emit: AppSignals.Emit("ino", "new"), Tone: ButtonTone.Accent) { Small = true, Icon = "plus" },
                ],
                Kind: SurfaceKind.Acrylic,
                Title: state.Title),
            new Divider("SESSIONS"),
            Ui.NavItem(
                $"session-{state.ConversationId}",
                state.Title,
                "sparkles",
                "open",
                selected: true,
                badge: $"{state.Turns.Length}"),
            new Collection([new Entry(state.ConversationId, state.Title, $"{state.Turns.Length} turn(s)")]),
            new Divider("WHAT INO MAY DO"),
            new Collection([.. (state.GrantSummaries ?? []).Select((summary, i) => new Entry($"grant-{i}", summary, null))]),
        ])
        { Gap = 8 });

    public static View Render(InoAppState state, AiMessage[] transcript, Approval[]? approvals = null, bool smart = false)
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
                        new Row(
                        [
                            new Text($"Smart mode: {(smart ? "on" : "off")}") { Tone = "muted" },
                            new Button("smart", smart ? "Turn smart mode off" : "Turn smart mode on", Emit: AppSignals.Emit("ino", "smart", smart ? "false" : "true")) { Small = true, Style = "ghost" },
                            .. state.ActiveRunId is not null ? new Node[] { new Button("stop", "Stop", Emit: AppSignals.Emit("ino", "stop")) { Small = true, Icon = "square" } } : [],
                        ]) { Gap = 8 },
                    ],
                    Kind: SurfaceKind.Acrylic,
                    Title: state.Title),
                .. state.Blocked ? new Node[] { new InfoBar("Ino is paused", state.BlockedReason ?? "An interrupt holds the agent.", "critical") } : [],
                .. (approvals ?? []).Select(RenderApproval),
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
                ChatNodes.Transcript([.. transcript.Select(ToEntry).OfType<ChatEntry>()]),
                ChatNodes.Composer(ComposerKey, ComposerField, AppSignals.Emit("ino", "send"), "Message Ino"),
            ])
            { Gap = 12 });
    }

    // The composer is the field neuron apps/ino/composer; its submit sends the text to the agent.
    public const string ComposerKey = "composer";
    public const string ComposerField = "apps/ino/composer";

    private static ChatEntry? ToEntry(AiMessage message, int index)
    {
        var text = string.Join("\n", message.Content.OfType<AiText>().Select(t => t.Text).Where(t => !string.IsNullOrWhiteSpace(t)));
        if (text.Length == 0 || message.Role is not ("user" or "assistant")) { return null; }
        return new ChatEntry($"message-{index}", message.Role == "user" ? ChatRole.User : ChatRole.Assistant, text);
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

    // Answers go straight to the framework's confirmations neuron, which stamps the person who clicked; Ino never relays them.
    private static Node RenderApproval(Approval approval) => new ApprovalCardNode(
        $"approval-{approval.PlanId}",
        approval.PlanId,
        approval.Title,
        approval.Detail,
        [new ApprovalRowNode("Subject", "", approval.Subject)],
        SystemName: "Ino",
        Status: "pending",
        Resolve: new Resolve(approval.PlanId));

    private static Node RenderDockedPromptBanner(InoCard card) => new Surface(
        [
            new Row(
            [
                new TagPill("Docked Waiting Card", "warn", "keyboard"),
                new Text($"Waiting on you · Docked below · Keyboard: ↵ Approve/Confirm · Esc Deny/Skip · {card.Title}") { Tone = "strong", Tabular = true },
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
                $"card-{card.Id}",
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
                SourceNeuronUri: card.SourceNeuronUri);
        }
        else if (card.Kind == "choice")
        {
            yield return new ChoiceCardNode(
                card.Id,
                card.Title,
                [
                    new ChoiceOptionNode("Nordlys Enterprise (Primary)", "neuron://local/circles/enterprise"),
                    new ChoiceOptionNode("Executive Board Circle", "neuron://local/circles/board"),
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
                Status: card.Status == "approved" ? "sent" : card.Status);
        }
        else if (card.Kind == "secret")
        {
            yield return new SecretCardNode(
                card.Title,
                card.Summary,
                SavedIn: "Secure Vault · neuron://local/vault/secrets",
                Status: card.Status == "approved" ? "saved" : card.Status,
                Enter: new Button("enter-secret", "Save to vault", Intent: AppSignals.Open("os://window/secure")));
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
                Open: new Button("review-install", "Review & Install", Intent: AppSignals.Open("os://sheet/install")));
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
        else if (card.Kind == "markdraw")
        {
            var activeDoc = card.Columns is { Length: >= 1 } && !string.IsNullOrWhiteSpace(card.Columns[0])
                ? card.Columns[0]
                : "/work/architecture.markdraw";
            var format = card.Columns is { Length: >= 2 } && !string.IsNullOrWhiteSpace(card.Columns[1])
                ? card.Columns[1]
                : "markdraw";
            var rev = card.Columns is { Length: >= 3 } && int.TryParse(card.Columns[2], out var parsedRev)
                ? parsedRev
                : 1;
            var content = card.Columns is { Length: >= 4 } && !string.IsNullOrWhiteSpace(card.Columns[3])
                ? card.Columns[3]
                : MarkdrawApp.DefaultArchitectureScene;
            var exportPath = card.Columns is { Length: >= 5 } && !string.IsNullOrWhiteSpace(card.Columns[4])
                ? card.Columns[4]
                : null;
            var elements = MarkdrawSceneEngine.ParseSceneElements(content);

            yield return new RecordCardNode(
                "Markdraw · Diagram & Canvas Studio",
                card.Title,
                [
                    new RecordFieldNode("Status", card.Status),
                    new RecordFieldNode("Workspace File", activeDoc),
                    new RecordFieldNode("Elements", $"{elements.Length} item(s) · rev {rev}"),
                ],
                Subtitle: card.Summary,
                LinkLabel: "Open Markdraw Studio",
                Open: new Button("open-markdraw", "Open Markdraw Studio", Intent: AppSignals.OpenApp("markdraw", activeDoc)));

            yield return MarkdrawSceneEngine.BuildMarkdrawCanvasCard(
                key: $"{card.Id}-canvas-rev-{rev}",
                title: card.Title,
                activeDocument: activeDoc,
                format: format,
                content: content,
                revision: rev,
                status: card.Summary,
                elements: elements,
                workspaceDrawings: [activeDoc, "/work/architecture.markdraw", "/work/pipeline-flow.markdraw"],
                lastExportPath: exportPath,
                height: 460);
        }
        else if (IntoChat.OS.Modules.ArtCraft.ArtCraftCatalog.TryGet(card.Kind, out var studioDescriptor))
        {
            yield return new RecordCardNode(
                $"{studioDescriptor.Title} · {studioDescriptor.Subtitle}",
                card.Title,
                [
                    new RecordFieldNode("Status", card.Status),
                    new RecordFieldNode("Studio Neuron", card.SourceNeuronUri),
                    new RecordFieldNode("Scope", card.EffectiveScopeUri),
                ],
                Subtitle: card.Summary,
                LinkLabel: $"Open {studioDescriptor.Title} Studio",
                Open: new Button($"open-{studioDescriptor.Id}", $"Open {studioDescriptor.Title} Studio", Intent: AppSignals.OpenApp(studioDescriptor.Id)));
            var webUrl = studioDescriptor.Id is "photocraft" or "filmcraft"
                ? studioDescriptor.DefaultWebUrl.TrimEnd('/')
                : studioDescriptor.StaticWebPath;
            yield return Ui.WebBrowser(webUrl, card.Title, $"{card.Id}-studio", 420, "wasm-ui");
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
                Open: card.Kind == "file" ? new Button("open-files", "Open in Files", Intent: AppSignals.OpenApp("files")) : null);
        }
    }
}
