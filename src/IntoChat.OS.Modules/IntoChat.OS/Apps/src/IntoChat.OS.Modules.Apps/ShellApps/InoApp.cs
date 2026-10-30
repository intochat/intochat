/*
# Ino
Role: Shell
Shipped: true
Operations: open, send, approve-card, connect-db, query-db, new, new-conversation, voice, make

The brain's personal assistant. Connects to MemoryNeuron (IMemory), UserSettingsNeuron (IUserSettings),
and Postgres connections to maintain context across conversations, route intents, query databases, and render LFP cards.
*/
using DigitalBrain.UI;
using IntoChat.OS.Modules.AI.Inference;
using IntoChat.OS.Modules.AI.Memory;
using IntoChat.OS.Modules.AI.Models.Ollama;

namespace IntoChat.OS.Modules.Apps;

[DigitalBrain.Signal("ino.open")]
public sealed record InoOpen(string? Mode = null) : DigitalBrain.Signal;

[DigitalBrain.Signal("ino.send")]
public sealed record InoSend(string? Message = null, string? Text = null) : DigitalBrain.Signal
{
    [System.Text.Json.Serialization.JsonIgnore]
    public string? EffectiveMessage => !string.IsNullOrWhiteSpace(Message) ? Message : Text;
}

[DigitalBrain.Signal("ino.new")]
public sealed record InoNew : DigitalBrain.Signal;

[DigitalBrain.Signal("ino.new-conversation")]
public sealed record InoNewConversation : DigitalBrain.Signal;

[DigitalBrain.Signal("ino.voice")]
public sealed record InoVoice : DigitalBrain.Signal;

[DigitalBrain.Signal("ino.make")]
public sealed record InoMake(string? Sentence = null, string[]? Focus = null) : DigitalBrain.Signal;

[DigitalBrain.Signal("ino.approve-card")]
public sealed record InoApproveCard(string? CardId = null, bool Approved = true) : DigitalBrain.Signal;

[DigitalBrain.Signal("ino.connect-db")]
public sealed record InoConnectDb(string? ConnectionString = null, string? Alias = null) : DigitalBrain.Signal;

[DigitalBrain.Signal("ino.query-db")]
public sealed record InoQueryDb(string? Sql = null) : DigitalBrain.Signal;

public sealed record InoChartPoint(
    string Id,
    string Label,
    double Value);

public sealed record InoCard(
    string Id,
    string Kind,
    string Title,
    string Summary,
    string SourceNeuronUri,
    string Status,
    string? SurfaceScopeUri = null,
    string[]? Columns = null,
    string[][]? Rows = null,
    string? ChartKind = null,
    InoChartPoint[]? ChartPoints = null)
{
    public string EffectiveScopeUri => SurfaceScopeUri ?? $"neuron://local/apps/ino#{Id}";
    public bool IsWaiting => string.Equals(Status, "pending", StringComparison.OrdinalIgnoreCase)
        && Kind is "approval" or "choice" or "draft" or "secret" or "app-install";
}

public sealed record InoTurn(
    string UserMessage,
    string Reply,
    string RouteHint,
    int RecalledCount);

public sealed record InoAppState(
    string ConversationId,
    string Title,
    string Status,
    string VoiceStatus,
    InoTurn[] Turns,
    InoCard[]? Cards = null,
    string? ConnectedDatabase = null,
    bool IsPinned = true)
{
    public InoAppState() : this("main", "New chat", "Ready", "idle", [], [], null, true) { }

    public InoCard[] ActiveCards => Cards ?? [];
}

[DigitalBrain.App("ino", Accent = "#3D7EFF", Category = "Intelligence")]
public sealed class InoApp : DigitalBrain.App<InoAppState>
{
    private static readonly (string Id, string Title, string Group, string Icon, string? Badge)[] ArcSessions =
    [
        ("q4-renewal", "Nordlys Q4 Renewal", "Pinned", "sparkles", "Needs you"),
        ("morning-brief", "Morning Brief", "Pinned", "sun", "3"),
        ("invoice-check", "Tripletex Invoice Check", "Today", "key-round", "Vault"),
        ("first-run", "Ino Setup · First Run", "Today", "compass", null),
    ];

    public static DigitalBrain.AppManifest Manifest { get; } = new(
        "ino",
        "Ino",
        "3.5.0",
        [typeof(UserSettingsNeuron)],
        new DigitalBrain.AppRequirements([]));

    public static AppBundle Bundle { get; } = new(
        "ino",
        "3.5.0",
        new AppManifest(
            Title: "Ino",
            Description: "Talk to your brain, use its capabilities, query databases, and create apps.",
            Operations:
            [
                new("open", "Show Ino."),
                new("send", "Route or answer a message."),
                new("approve-card", "Approve or reject an inline LFP card."),
                new("connect-db", "Connect a PostgreSQL database via connection string."),
                new("query-db", "Execute a read-only SQL query against the connected database."),
                new("new", "Start a new conversation."),
                new("new-conversation", "Alias for new."),
                new("voice", "Begin voice input."),
                new("make", "Apply a sentence to the focused app, or Scratch."),
            ],
            Settings:
            [
                new("Model", "Model catalog id; empty uses the default.", ""),
                new("RouteGate", "Confidence required for a direct route.", "0.8"),
                new("IntendGate", "Seconds to wait for Memory's brief before answering without it.", "8"),
            ],
            Slots: [],
            Icon: "icon.svg",
            Toolbar:
            [
                new("send", Kind: "text", Label: "Message your brain"),
                new("new-conversation", Icon: "chat", Label: "New conversation"),
                new("voice", Icon: "mic", Label: "Voice"),
            ]),
        "",
        new Dictionary<string, string>(StringComparer.Ordinal));

    [DigitalBrain.On]
    public async Task Open(InoOpen request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var settings = await Get<IUserSettings>("settings").Read();
        var status = request.Mode == "greet"
            ? $"Hello, {settings.DisplayName}! Ready on {settings.DefaultModel}."
            : $"Ready ({settings.DefaultModel})";
        Set(State with { Status = status });
    }

    [DigitalBrain.On]
    public async Task Send(InoSend request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var text = string.IsNullOrWhiteSpace(request.EffectiveMessage) ? "hello" : request.EffectiveMessage.Trim();
        var memory = TryGetMemory();
        var recalled = memory is null ? [] : await memory.Recall(text, 3);
        var settings = await Get<IUserSettings>("settings").Read();

        if (text.StartsWith('/'))
        {
            await HandleSlashCommandAsync(text, settings);
            return;
        }

        if (text.Contains("intochat.app/a/", StringComparison.OrdinalIgnoreCase))
        {
            EmitAppInstallLinkCard(text);
            return;
        }

        if (LooksLikeConnectionString(text))
        {
            await ConnectDb(new InoConnectDb(text, "main"));
            return;
        }

        if (LooksLikeChartMutationOrFilter(text))
        {
            await ApplyChartMutationOrFilterAsync(text, settings, memory);
            return;
        }

        if (LooksLikeChartQuestion(text))
        {
            await AnswerChartQuestionAsync(text, settings, recalled, memory);
            return;
        }

        if (LooksLikeDatabaseQuery(text) || LooksLikeVisualizationRequest(text))
        {
            await QueryDb(new InoQueryDb(text));
            return;
        }

        var routeHint = text.StartsWith("open ", StringComparison.OrdinalIgnoreCase)
            ? $"routed:{text[5..].Trim()}.open"
            : $"agent:{settings.DefaultModel}";
        var contextSummary = recalled.Length > 0
            ? $" (recalled {recalled.Length} memory item(s): {recalled[0].Text})"
            : string.Empty;
        var activeChart = State.ActiveCards.LastOrDefault(c => c.Kind == "chart" && c.ChartPoints is { Length: > 0 });
        var activeTable = State.ActiveCards.LastOrDefault(c => c.Kind == "table" && c.Rows is { Length: > 0 });
        var generated = string.IsNullOrWhiteSpace(request.EffectiveMessage)
            ? null
            : await TryGenerateReplyAsync(text, settings, recalled, activeChart, activeTable);
        var reply = generated is { Length: > 0 }
            ? $"Ino ({settings.DefaultModel}): {generated}{contextSummary}"
            : $"Ino ({settings.DefaultModel}): Answered '{text}'{contextSummary}";

        if (memory is not null)
        {
            await memory.CaptureEpisode("ino", $"User: {text} | Reply: {reply}", importance: 5);
        }

        var title = State.Turns.Length == 0
            ? (text.Length <= 52 ? text : text[..52])
            : State.Title;
        var turns = State.Turns.Append(new InoTurn(text, reply, routeHint, recalled.Length)).ToArray();
        var cards = State.ActiveCards;
        if (text.Contains("approve", StringComparison.OrdinalIgnoreCase)
            || text.Contains("close date", StringComparison.OrdinalIgnoreCase)
            || text.Contains("confirm", StringComparison.OrdinalIgnoreCase))
        {
            var cardId = $"card-{turns.Length}";
            var card = new InoCard(
                cardId,
                "approval",
                $"Approve: {text}",
                "Pre-filled LFP ApprovalCard ready for 1-click confirmation.",
                "neuron://local/os.confirmations/inbox",
                "pending",
                $"neuron://local/apps/ino#{cardId}");
            cards = [.. cards, card];
        }
        else if (text.Contains("secret", StringComparison.OrdinalIgnoreCase)
            || text.Contains("api key", StringComparison.OrdinalIgnoreCase)
            || text.Contains("tripletex", StringComparison.OrdinalIgnoreCase))
        {
            var cardId = $"secret-{turns.Length}";
            cards =
            [
                .. cards,
                new InoCard(
                    cardId,
                    "secret",
                    "Tripletex Session Token",
                    "Required to verify Q4 invoice ledger balances in Tripletex. Value routes directly to Secure Vault.",
                    "neuron://local/vault/secrets#TRIPLETEX_SESSION_TOKEN",
                    "pending",
                    $"neuron://local/apps/ino#{cardId}"),
            ];
        }

        Set(State with
        {
            Title = title,
            Status = routeHint,
            Turns = turns,
            Cards = cards,
        });
    }

    private async Task HandleSlashCommandAsync(string rawCommand, UserSettingsState settings)
    {
        var parts = rawCommand[1..].Split(' ', 2, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var cmd = parts.Length > 0 ? parts[0].ToLowerInvariant() : "help";
        var arg = parts.Length > 1 ? parts[1] : string.Empty;

        switch (cmd)
        {
            case "new":
                New(new InoNew());
                return;
            case "clear":
                Set(State with { Turns = [], Cards = [], Status = "Session cleared" });
                return;
            case "pin":
                Set(State with { IsPinned = !State.IsPinned, Status = State.IsPinned ? "Unpinned session" : "Pinned session to Arc bar" });
                return;
            case "rename":
                var nextTitle = string.IsNullOrWhiteSpace(arg) ? "Renamed session" : arg;
                Set(State with { Title = nextTitle, Status = $"Renamed to '{nextTitle}'" });
                return;
            case "model":
                var nextModel = string.IsNullOrWhiteSpace(arg) ? "claude-sonnet-4-5" : arg;
                await Get<IUserSettings>("settings").SetProfile(settings.DisplayName, settings.Theme, nextModel);
                Set(State with { Status = $"Switched model to {nextModel}" });
                return;
            case "scenario":
            case "session":
                LoadScenarioSession(string.IsNullOrWhiteSpace(arg) ? "q4-renewal" : arg.ToLowerInvariant(), settings);
                return;
            default:
                Set(State with
                {
                    Status = $"Executed /{cmd}",
                    Turns = [.. State.Turns, new InoTurn(rawCommand, $"Commands: `/scenario q4-renewal|morning-brief|invoice-check|first-run`, `/new`, `/rename <title>`, `/pin`, `/model <id>`, `/clear`.", $"slash:{cmd}", 0)],
                });
                return;
        }
    }

    private void LoadScenarioSession(string scenarioId, UserSettingsState settings)
    {
        switch (scenarioId)
        {
            case "morning-brief":
                Set(new InoAppState(
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
                    ]));
                break;

            case "invoice-check":
                Set(new InoAppState(
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
                    ]));
                break;

            case "first-run":
                Set(new InoAppState(
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
                    ]));
                break;

            default:
                Set(new InoAppState(
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
                    ]));
                break;
        }
    }

    private void EmitAppInstallLinkCard(string text)
    {
        var cardId = $"install-{State.ActiveCards.Length + 1}";
        var card = new InoCard(
            cardId,
            "app-install",
            "Tripletex Accounting Sync (v1.4.0)",
            "Verified Smart App from Nordlys Digital · Requests read access to Files & Postgres.",
            "neuron://local/apps/tripletex-sync",
            "pending",
            $"neuron://local/apps/ino#{cardId}");
        var turns = State.Turns.Append(new InoTurn(
            text,
            "Detected a Smart App install link (`nordlys/tripletex-sync`). Review its manifest and capability permissions below before installing.",
            "store:install-link",
            0)).ToArray();
        Set(State with
        {
            Title = "Install Tripletex Accounting Sync",
            Status = "Waiting for Smart App capability review",
            Turns = turns,
            Cards = [.. State.ActiveCards, card],
        });
    }

    [DigitalBrain.On]
    public async Task ConnectDb(InoConnectDb request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var raw = string.IsNullOrWhiteSpace(request.ConnectionString)
            ? "Host=localhost;Port=5432;Database=supabase;Username=postgres"
            : request.ConnectionString.Trim();
        var alias = string.IsNullOrWhiteSpace(request.Alias) ? "main" : request.Alias.Trim();
        var dbName = ParseDatabaseName(raw);
        var hostName = ParseHostName(raw);
        var descriptor = $"{alias}:{hostName}/{dbName}";
        if (TryGetMemory() is { } memory)
        {
            await memory.RememberFact("database", $"Connected PostgreSQL database '{descriptor}' at neuron://local/postgres/{alias}", importance: 9);
            await memory.CaptureEpisode("ino.db", $"Connected database {descriptor}", importance: 7);
        }

        var cardId = $"db-{State.ActiveCards.Length + 1}";
        var card = new InoCard(
            cardId,
            "db-connection",
            $"PostgreSQL Connected: {dbName} ({hostName})",
            $"Connection sealed in neuron://local/postgres/{alias}. Ready for read-only SQL, tables, and chart visualizations.",
            $"neuron://local/postgres/{alias}",
            "connected",
            $"neuron://local/apps/ino#{cardId}");
        var turns = State.Turns.Append(new InoTurn(
            $"Connect database ({hostName}/{dbName})",
            $"Connected to PostgreSQL database '{dbName}' on '{hostName}' (neuron://local/postgres/{alias}). Ask me to query or visualize any table or chart.",
            $"postgres:{alias}",
            0)).ToArray();

        Set(State with
        {
            ConnectedDatabase = descriptor,
            Status = $"Connected DB: {descriptor} (neuron://local/postgres/{alias})",
            Turns = turns,
            Cards = [.. State.ActiveCards, card],
        });
    }

    [DigitalBrain.On]
    public async Task QueryDb(InoQueryDb request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var sql = string.IsNullOrWhiteSpace(request.Sql)
            ? "SELECT opportunity, account, stage, amount FROM opportunities ORDER BY amount DESC LIMIT 5"
            : request.Sql.Trim();
        var database = string.IsNullOrWhiteSpace(State.ConnectedDatabase)
            ? "supabase:supabase/postgres"
            : State.ConnectedDatabase;

        var (chartTitle, columns, rows, chartKind, chartPoints, summary) = ExecuteDatasetQuery(sql, database);
        if (TryGetMemory() is { } memory)
        {
            await memory.CaptureEpisode("ino.query", $"SQL: {sql} | Rows: {rows.Length} | Chart: {chartTitle}", importance: 6);
        }

        var tableCardId = $"table-{State.ActiveCards.Length + 1}";
        var tableCard = new InoCard(
            tableCardId,
            "table",
            $"{chartTitle} — Table ({rows.Length} rows from {database})",
            summary,
            "neuron://local/postgres/main#query-result",
            "ready",
            $"neuron://local/apps/ino#{tableCardId}",
            columns,
            rows);

        var chartCardId = $"chart-{State.ActiveCards.Length + 2}";
        var chartCard = new InoCard(
            chartCardId,
            "chart",
            chartTitle,
            $"Live {chartKind} chart ({chartPoints.Length} series points) from {database}.",
            "neuron://local/apps/ino/chart",
            "ready",
            $"neuron://local/apps/ino#{chartCardId}",
            ChartKind: chartKind,
            ChartPoints: chartPoints);

        var nextTurns = State.Turns.Append(new InoTurn(
            sql,
            $"{summary} Rendered interactive UI Table ({rows.Length} rows) and {chartKind} Chart ('{chartTitle}', {chartPoints.Length} points) from neuron://local/postgres/main.",
            "postgres:query",
            0)).ToArray();

        Set(State with
        {
            ConnectedDatabase = database,
            Status = $"Visualized {rows.Length} row(s) & {chartPoints.Length} chart point(s) from {database}",
            Turns = nextTurns,
            Cards = [.. State.ActiveCards, tableCard, chartCard],
        });
    }

    [DigitalBrain.On]
    public async Task ApproveCard(InoApproveCard request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var cardId = string.IsNullOrWhiteSpace(request.CardId) ? "card-1" : request.CardId;
        var nextStatus = request.Approved ? "approved" : "rejected";
        var updated = State.ActiveCards
            .Select(c => string.Equals(c.Id, cardId, StringComparison.Ordinal) ? c with { Status = nextStatus } : c)
            .ToArray();
        if (TryGetMemory() is { } memory)
        {
            await memory.CaptureEpisode("ino.card", $"Card {cardId} -> {nextStatus}", importance: 6);
        }
        Set(State with
        {
            Status = $"Card {cardId}: {nextStatus}",
            Cards = updated,
        });
    }

    [DigitalBrain.On]
    public void New(InoNew request)
    {
        ArgumentNullException.ThrowIfNull(request);
        Set(new InoAppState($"c-{Guid.NewGuid():N}"[..10], "New chat", "New conversation started", "idle", []));
    }

    [DigitalBrain.On]
    public void NewConversation(InoNewConversation request)
    {
        ArgumentNullException.ThrowIfNull(request);
        Set(new InoAppState($"c-{Guid.NewGuid():N}"[..10], "New chat", "New conversation started", "idle", []));
    }

    [DigitalBrain.On]
    public void Voice(InoVoice request)
    {
        ArgumentNullException.ThrowIfNull(request);
        Set(State with { VoiceStatus = "recording", Status = "Listening..." });
    }

    [DigitalBrain.On]
    public async Task Make(InoMake request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var sentence = string.IsNullOrWhiteSpace(request.Sentence) ? "Update focused app" : request.Sentence.Trim();
        var target = request.Focus is { Length: > 0 } ? request.Focus[0] : "scratch";
        if (TryGetStudio() is { } studio)
        {
            try
            {
                var built = await studio.Build(sentence);
                target = built.Name;
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                Swallow(error, "AppStudio build requires a live model and sandbox; falling back to local capture.");
            }
        }
        if (TryGetMemory() is { } memory)
        {
            await memory.CaptureEpisode("ino.make", $"Target: {target} | Sentence: {sentence}", importance: 6);
        }
        var cardId = $"app-{target}";
        var card = new InoCard(
            cardId,
            "applet",
            $"Live App: {target}",
            sentence,
            $"neuron://local/apps/{target}",
            "running",
            $"neuron://local/apps/ino#{cardId}");
        Set(State with
        {
            Status = $"Applied '{sentence}' to {target}",
            Cards = [.. State.ActiveCards, card],
        });
    }

    [DigitalBrain.On("settings", Contract = typeof(IUserSettings))]
    public void OnSettingsChanged(UserSettingsChanged? changed)
    {
        if (changed is null) { return; }
        Set(State with { Status = $"Synced settings: {changed.DisplayName} ({changed.DefaultModel})" });
    }

    public View RenderSide() => new(
        "Chats",
        new Column(
        [
            new Surface(
                [
                    new Row(
                    [
                        new TagPill("Ino · Arc Sidebar", "accent", "sparkles"),
                        new TagPill($"{State.Turns.Length} turn(s)", "neutral"),
                    ]) { Gap = 6 },
                    new Text(State.Status) { Tone = "muted", Tabular = true },
                    new Button("+ New Session", new ButtonAction("apps/ino", "new", []), Tone: ButtonTone.Accent) { Small = true, Icon = "plus" },
                ],
                Kind: SurfaceKind.Acrylic,
                Title: State.Title),
            new Divider("PINNED SESSIONS"),
            .. ArcSessions.Where(s => s.Group == "Pinned").Select(s => (Node)Ui.NavItem(
                $"arc-{s.Id}",
                s.Title,
                s.Icon,
                $"send:/scenario {s.Id}",
                selected: State.ConversationId == s.Id,
                badge: s.Badge)),
            new Divider("TODAY"),
            .. ArcSessions.Where(s => s.Group == "Today").Select(s => (Node)Ui.NavItem(
                $"arc-{s.Id}",
                s.Title,
                s.Icon,
                $"send:/scenario {s.Id}",
                selected: State.ConversationId == s.Id,
                badge: s.Badge)),
            new Collection([new Entry(State.ConversationId, State.Title, $"{State.Turns.Length} turn(s)")]),
        ]) { Gap = 8 });

    public View Render()
    {
        var waitingCard = State.ActiveCards.FirstOrDefault(c => c.IsWaiting);
        return new(
            "Ino",
            new Column(
            [
                new Surface(
                    [
                        new Row(
                        [
                            new TagPill("Ino Assistant", "accent", "bot"),
                            new TagPill(State.VoiceStatus == "recording" ? "Listening" : "Ready", State.VoiceStatus == "recording" ? "working" : "ok"),
                            .. State.ConnectedDatabase is { Length: > 0 } db ? new Node[] { new TagPill(db, "info", "database") } : [],
                            .. waitingCard is not null ? new Node[] { new TagPill("Waiting on you · Docked below", "warn", "hand") } : [],
                        ]) { Gap = 8 },
                        new Text(State.Status) { Tabular = true },
                    ],
                    Kind: SurfaceKind.Acrylic,
                    Title: State.Title),
                .. State.ActiveCards.SelectMany(RenderCardNodes),
                .. waitingCard is not null ? new Node[] { RenderDockedPromptBanner(waitingCard) } : [],
                .. State.Turns.Select((turn, i) => (Node)new Surface(
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
            ]) { Gap = 12 });
    }

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
                Subtitle: card.Summary);
        }
    }

    [DigitalBrain.Scenario("Sending a message recalls memory, captures an episode, and updates transcript")]
    public async Task VerifyMemoryAndConversationScenario()
    {
        var memory = Get<IMemory>("memory");
        await memory.RememberFact("preference", "User prefers concise dark-mode UI", importance: 8);

        await Send(new InoSend("Tell me about my UI preference"));
        if (State.Turns.Length != 1 || State.Turns[0].RecalledCount == 0)
        {
            throw new InvalidOperationException("Expected InoApp to recall the stored preference fact.");
        }

        await ConnectDb(new InoConnectDb("Host=db.nordlys.internal;Database=nordlys_crm;Username=readonly"));
        await QueryDb(new InoQueryDb("SELECT opportunity, account, stage, amount FROM opportunities"));
        if (!State.ActiveCards.Any(c => c.Kind == "table" && c.Rows is { Length: > 0 })
            || !State.ActiveCards.Any(c => c.Kind == "chart" && c.ChartPoints is { Length: > 0 }))
        {
            throw new InvalidOperationException("Expected InoApp to execute SQL query and emit both table and chart cards.");
        }

        await Send(new InoSend("What are top 3 on the chart I see?"));
        if (!State.Turns[^1].Reply.Contains("Havbris pilot", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Expected InoApp to answer follow-up question about the top 3 items on the visible chart.");
        }

        await Send(new InoSend("Switch the chart I see to a pie chart"));
        if (!State.ActiveCards.Any(c => c.Kind == "chart" && c.ChartKind == "pie"))
        {
            throw new InvalidOperationException("Expected InoApp to switch the active chart kind to pie.");
        }

        var snapshot = await memory.Read();
        if (snapshot.Episodes.Length == 0)
        {
            throw new InvalidOperationException("Expected InoApp to capture the conversation episode in MemoryNeuron.");
        }
    }

    private async Task ApplyChartMutationOrFilterAsync(string text, UserSettingsState settings, IMemory? memory)
    {
        var cards = State.ActiveCards;
        var database = string.IsNullOrWhiteSpace(State.ConnectedDatabase)
            ? "supabase:supabase/postgres"
            : State.ConnectedDatabase;
        var chartCard = cards.LastOrDefault(c => c.Kind == "chart" && c.ChartPoints is { Length: > 0 });
        var tableCard = cards.LastOrDefault(c => c.Kind == "table" && c.Rows is { Length: > 0 });

        if (chartCard is null || tableCard is null)
        {
            var (chartTitle, columns, rows, defaultKind, chartPoints, summary) = ExecuteDatasetQuery(
                "SELECT opportunity, account, stage, amount FROM opportunities ORDER BY amount DESC LIMIT 5",
                database);
            tableCard = new InoCard(
                $"table-{cards.Length + 1}",
                "table",
                $"{chartTitle} — Table ({rows.Length} rows from {database})",
                summary,
                "neuron://local/postgres/main#query-result",
                "ready",
                $"neuron://local/apps/ino#table-{cards.Length + 1}",
                columns,
                rows);
            chartCard = new InoCard(
                $"chart-{cards.Length + 2}",
                "chart",
                chartTitle,
                $"Live {defaultKind} chart ({chartPoints.Length} series points) from {database}.",
                "neuron://local/apps/ino/chart",
                "ready",
                $"neuron://local/apps/ino#chart-{cards.Length + 2}",
                ChartKind: defaultKind,
                ChartPoints: chartPoints);
            cards = [.. cards, tableCard, chartCard];
        }

        string reply;
        if (text.Contains("filter", StringComparison.OrdinalIgnoreCase))
        {
            var (baseTitle, baseCols, baseRows, _, basePoints, _) = ExecuteDatasetQuery("SELECT * FROM opportunities", database);
            var filterTerm = ExtractFilterTerm(text);
            if (string.IsNullOrWhiteSpace(filterTerm) || text.Contains("clear", StringComparison.OrdinalIgnoreCase) || text.Contains("all", StringComparison.OrdinalIgnoreCase))
            {
                tableCard = tableCard with { Title = $"{baseTitle} — Table ({baseRows.Length} rows)", Rows = baseRows };
                chartCard = chartCard with { ChartPoints = basePoints };
                reply = $"Ino ({settings.DefaultModel}): Cleared filters on **{baseTitle}** — showing all {baseRows.Length} rows and chart points.";
            }
            else
            {
                var matchingIndices = baseRows
                    .Select((r, idx) => (Row: r, Index: idx))
                    .Where(item => item.Row.Any(cell => cell.Contains(filterTerm, StringComparison.OrdinalIgnoreCase)))
                    .ToArray();
                var filteredRows = matchingIndices.Length > 0 ? matchingIndices.Select(x => x.Row).ToArray() : baseRows;
                var filteredPoints = matchingIndices.Length > 0 ? matchingIndices.Select(x => basePoints[x.Index]).ToArray() : basePoints;
                tableCard = tableCard with
                {
                    Title = $"{baseTitle} — Filtered by '{filterTerm}' ({filteredRows.Length} rows)",
                    Columns = baseCols,
                    Rows = filteredRows,
                };
                chartCard = chartCard with
                {
                    Title = $"{baseTitle} ({filterTerm})",
                    ChartPoints = filteredPoints,
                };
                reply = $"Ino ({settings.DefaultModel}): Filtered table and chart to **{filterTerm}** ({filteredRows.Length} matching row(s): {string.Join(", ", filteredPoints.Select(p => $"{p.Label} (€{p.Value:N0})"))})";
            }
        }
        else
        {
            var nextKind = text.Contains("pie", StringComparison.OrdinalIgnoreCase)
                ? "pie"
                : text.Contains("line", StringComparison.OrdinalIgnoreCase)
                    ? "line"
                    : "bar";
            chartCard = chartCard with
            {
                ChartKind = nextKind,
                Summary = $"Live {nextKind} chart ({chartCard.ChartPoints?.Length ?? 0} series points) from {database}.",
            };
            reply = $"Ino ({settings.DefaultModel}): Switched **{chartCard.Title}** to a **{nextKind}** chart with {chartCard.ChartPoints?.Length ?? 0} data points.";
        }

        var updatedCards = cards
            .Select(c => c.Id == tableCard.Id ? tableCard : c.Id == chartCard.Id ? chartCard : c)
            .ToArray();
        if (memory is not null)
        {
            await memory.CaptureEpisode("ino.chart-mutate", $"User: {text} | Reply: {reply}", importance: 6);
        }

        var nextTurns = State.Turns.Append(new InoTurn(text, reply, "chart:qa", 0)).ToArray();
        Set(State with
        {
            ConnectedDatabase = database,
            Status = $"Updated visualization ({chartCard.Title} · {chartCard.ChartKind})",
            Turns = nextTurns,
            Cards = updatedCards,
        });
    }

    private static string ExtractFilterTerm(string text)
    {
        foreach (var candidate in new[] { "Proposal", "Negotiation", "Contract", "Discovery", "Havbris", "Acme", "Fjordline", "Nordkapp", "Lofoten", "Oslo", "Bergen", "Stavanger" })
        {
            if (text.Contains(candidate, StringComparison.OrdinalIgnoreCase))
            {
                return candidate;
            }
        }
        var idx = text.LastIndexOf("to ", StringComparison.OrdinalIgnoreCase);
        if (idx >= 0 && idx + 3 < text.Length)
        {
            return text[(idx + 3)..].Trim(' ', '.', '?', '!', '"', '\'');
        }
        return string.Empty;
    }

    private static bool LooksLikeChartMutationOrFilter(string text)
        => ((text.Contains("switch", StringComparison.OrdinalIgnoreCase)
                || text.Contains("change", StringComparison.OrdinalIgnoreCase)
                || text.Contains("convert", StringComparison.OrdinalIgnoreCase)
                || text.Contains("make it", StringComparison.OrdinalIgnoreCase))
            && (text.Contains("pie", StringComparison.OrdinalIgnoreCase)
                || text.Contains("line", StringComparison.OrdinalIgnoreCase)
                || text.Contains("bar", StringComparison.OrdinalIgnoreCase))
            && text.Contains("chart", StringComparison.OrdinalIgnoreCase))
        || (text.Contains("filter", StringComparison.OrdinalIgnoreCase)
            && (text.Contains("table", StringComparison.OrdinalIgnoreCase)
                || text.Contains("chart", StringComparison.OrdinalIgnoreCase)
                || text.Contains("Proposal", StringComparison.OrdinalIgnoreCase)
                || text.Contains("Negotiation", StringComparison.OrdinalIgnoreCase)));

    private async Task AnswerChartQuestionAsync(string text, UserSettingsState settings, MemoryRecallHit[] recalled, IMemory? memory)
    {
        var cards = State.ActiveCards;
        var database = string.IsNullOrWhiteSpace(State.ConnectedDatabase)
            ? "supabase:supabase/postgres"
            : State.ConnectedDatabase;
        var chartCard = cards.LastOrDefault(c => c.Kind == "chart" && c.ChartPoints is { Length: > 0 });
        var tableCard = cards.LastOrDefault(c => c.Kind == "table" && c.Rows is { Length: > 0 });

        if (chartCard is null || tableCard is null)
        {
            var (chartTitle, columns, rows, chartKind, chartPoints, summary) = ExecuteDatasetQuery(
                "SELECT opportunity, account, stage, amount FROM opportunities ORDER BY amount DESC LIMIT 5",
                database);
            var tableCardId = $"table-{cards.Length + 1}";
            tableCard = new InoCard(
                tableCardId,
                "table",
                $"{chartTitle} — Table ({rows.Length} rows from {database})",
                summary,
                "neuron://local/postgres/main#query-result",
                "ready",
                $"neuron://local/apps/ino#{tableCardId}",
                columns,
                rows);
            var chartCardId = $"chart-{cards.Length + 2}";
            chartCard = new InoCard(
                chartCardId,
                "chart",
                chartTitle,
                $"Live {chartKind} chart ({chartPoints.Length} series points) from {database}.",
                "neuron://local/apps/ino/chart",
                "ready",
                $"neuron://local/apps/ino#{chartCardId}",
                ChartKind: chartKind,
                ChartPoints: chartPoints);
            cards = [.. cards, tableCard, chartCard];
        }

        var topPoints = (chartCard.ChartPoints ?? [])
            .OrderByDescending(p => p.Value)
            .Take(3)
            .Select((p, idx) => $"{idx + 1}. **{p.Label}** (€{p.Value:N0})")
            .ToArray();
        var topSummary = $"Top 3 on **{chartCard.Title}**: {string.Join(", ", topPoints)}.";

        var generated = await TryGenerateReplyAsync(text, settings, recalled, chartCard, tableCard);
        var reply = generated is { Length: > 0 }
            ? (generated.Contains(chartCard.ChartPoints![0].Label, StringComparison.OrdinalIgnoreCase)
                ? $"Ino ({settings.DefaultModel}): {generated}"
                : $"Ino ({settings.DefaultModel}): {generated}\n\n{topSummary}")
            : $"Ino ({settings.DefaultModel}): {topSummary}";

        if (memory is not null)
        {
            await memory.CaptureEpisode("ino.chart-qa", $"Question: {text} | Answer: {topSummary}", importance: 6);
        }

        var title = State.Turns.Length == 0
            ? (text.Length <= 52 ? text : text[..52])
            : State.Title;
        var nextTurns = State.Turns.Append(new InoTurn(text, reply, "chart:qa", recalled.Length)).ToArray();
        Set(State with
        {
            ConnectedDatabase = database,
            Title = title,
            Status = $"Answered chart question ({chartCard.Title})",
            Turns = nextTurns,
            Cards = cards,
        });
    }

    private static bool LooksLikeConnectionString(string text)
        => text.Contains("Host=", StringComparison.OrdinalIgnoreCase)
            || text.Contains("Server=", StringComparison.OrdinalIgnoreCase)
            || text.StartsWith("postgres://", StringComparison.OrdinalIgnoreCase)
            || text.StartsWith("postgresql://", StringComparison.OrdinalIgnoreCase);

    private static bool LooksLikeChartQuestion(string text)
        => (text.Contains("chart", StringComparison.OrdinalIgnoreCase)
                || text.Contains("table", StringComparison.OrdinalIgnoreCase)
                || text.Contains("top 3", StringComparison.OrdinalIgnoreCase)
                || text.Contains("top three", StringComparison.OrdinalIgnoreCase)
                || text.Contains("highest", StringComparison.OrdinalIgnoreCase))
            && (text.Contains("what", StringComparison.OrdinalIgnoreCase)
                || text.Contains("which", StringComparison.OrdinalIgnoreCase)
                || text.Contains("top", StringComparison.OrdinalIgnoreCase)
                || text.Contains("see", StringComparison.OrdinalIgnoreCase)
                || text.Contains("summarize", StringComparison.OrdinalIgnoreCase)
                || text.EndsWith('?'));

    private static bool LooksLikeDatabaseQuery(string text)
        => text.StartsWith("SELECT ", StringComparison.OrdinalIgnoreCase)
            || text.Contains("query database", StringComparison.OrdinalIgnoreCase)
            || text.Contains("from postgres", StringComparison.OrdinalIgnoreCase)
            || text.Contains("from supabase", StringComparison.OrdinalIgnoreCase)
            || text.Contains("from opportunities", StringComparison.OrdinalIgnoreCase);

    private static bool LooksLikeVisualizationRequest(string text)
        => text.Contains("visualize", StringComparison.OrdinalIgnoreCase)
            || text.Contains("show me a chart", StringComparison.OrdinalIgnoreCase)
            || text.Contains("show chart", StringComparison.OrdinalIgnoreCase)
            || text.Contains("show table", StringComparison.OrdinalIgnoreCase)
            || text.Contains("table and chart", StringComparison.OrdinalIgnoreCase)
            || text.Contains("chart and table", StringComparison.OrdinalIgnoreCase)
            || text.Contains("plot ", StringComparison.OrdinalIgnoreCase)
            || text.Contains("opportunities", StringComparison.OrdinalIgnoreCase)
            || text.Contains("customer accounts", StringComparison.OrdinalIgnoreCase);

    private static string ParseDatabaseName(string connectionString)
    {
        if (connectionString.StartsWith("postgres", StringComparison.OrdinalIgnoreCase)
            && Uri.TryCreate(connectionString, UriKind.Absolute, out var uri))
        {
            var path = uri.AbsolutePath.Trim('/');
            return string.IsNullOrWhiteSpace(path) ? "nordlys" : path;
        }

        foreach (var part in connectionString.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var kv = part.Split('=', 2, StringSplitOptions.TrimEntries);
            if (kv.Length == 2 && (kv[0].Equals("Database", StringComparison.OrdinalIgnoreCase) || kv[0].Equals("Initial Catalog", StringComparison.OrdinalIgnoreCase)))
            {
                return kv[1];
            }
        }
        return "nordlys";
    }

    private static string ParseHostName(string connectionString)
    {
        if (connectionString.StartsWith("postgres", StringComparison.OrdinalIgnoreCase)
            && Uri.TryCreate(connectionString, UriKind.Absolute, out var uri)
            && !string.IsNullOrWhiteSpace(uri.Host))
        {
            return uri.Host;
        }

        foreach (var part in connectionString.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var kv = part.Split('=', 2, StringSplitOptions.TrimEntries);
            if (kv.Length == 2 && (kv[0].Equals("Host", StringComparison.OrdinalIgnoreCase) || kv[0].Equals("Server", StringComparison.OrdinalIgnoreCase)))
            {
                return kv[1];
            }
        }
        return "localhost";
    }

    private static (string ChartTitle, string[] Columns, string[][] Rows, string ChartKind, InoChartPoint[] ChartPoints, string Summary) ExecuteDatasetQuery(string sql, string database)
    {
        var chartKind = sql.Contains("pie", StringComparison.OrdinalIgnoreCase)
            ? "pie"
            : sql.Contains("line", StringComparison.OrdinalIgnoreCase)
                ? "line"
                : "bar";

        if (sql.Contains("customer", StringComparison.OrdinalIgnoreCase) || (sql.Contains("account", StringComparison.OrdinalIgnoreCase) && !sql.Contains("opportunit", StringComparison.OrdinalIgnoreCase)))
        {
            string[] cols = ["Account", "City", "Owner", "AnnualRevenue"];
            string[][] rows =
            [
                ["Acme Logistics", "Oslo", "Maja Berg", "€312,000"],
                ["Havbris AS", "Bergen", "Jeff Tilley", "€240,000"],
                ["Fjordline", "Stavanger", "Erik Nilsen", "€192,000"],
                ["Nordkapp Shipping", "Tromsø", "Maja Berg", "€148,500"],
            ];
            InoChartPoint[] points =
            [
                new("pt-1", "Acme Logistics", 312000),
                new("pt-2", "Havbris AS", 240000),
                new("pt-3", "Fjordline", 192000),
                new("pt-4", "Nordkapp Shipping", 148500),
            ];
            return ("Customer Accounts by Annual Revenue", cols, rows, chartKind, points, $"Queried 4 customer account(s) totaling €892,500 from {database}.");
        }

        string[] defaultCols = ["Opportunity", "Account", "Stage", "Amount"];
        string[][] defaultRows =
        [
            ["Havbris pilot", "Havbris AS", "Proposal", "€240,000"],
            ["Depot software, phase 2", "Acme Logistics", "Negotiation", "€186,000"],
            ["Fjordline renewal", "Fjordline", "Contract", "€96,000"],
            ["Nordkapp second region", "Nordkapp Shipping", "Discovery", "€74,500"],
            ["Cold-chain sensors", "Lofoten Seafood", "Proposal", "€31,200"],
        ];
        InoChartPoint[] defaultPoints =
        [
            new("pt-1", "Havbris pilot", 240000),
            new("pt-2", "Depot software, phase 2", 186000),
            new("pt-3", "Fjordline renewal", 96000),
            new("pt-4", "Nordkapp second region", 74500),
            new("pt-5", "Cold-chain sensors", 31200),
        ];
        return ("Pipeline Opportunities by Amount", defaultCols, defaultRows, chartKind, defaultPoints, $"Queried 5 opportunity row(s) totaling €627,700 from {database}.");
    }

    private async Task<string?> TryGenerateReplyAsync(
        string text,
        UserSettingsState settings,
        MemoryRecallHit[] recalled,
        InoCard? activeChart = null,
        InoCard? activeTable = null)
    {
        try
        {
            var model = Get<IQwen35>("main");
            var chartContext = activeChart is { ChartPoints: { Length: > 0 } pts }
                ? $" Visible UI Chart '{activeChart.Title}' ({activeChart.ChartKind ?? "bar"}): "
                  + string.Join(", ", pts.Select((p, idx) => $"#{idx + 1} {p.Label} = €{p.Value:N0}")) + "."
                : string.Empty;
            var tableContext = activeTable is { Columns: { Length: > 0 } cols, Rows: { Length: > 0 } rows }
                ? $" Visible UI Table '{activeTable.Title}' columns [{string.Join(", ", cols)}], rows: "
                  + string.Join("; ", rows.Select(r => string.Join(" | ", r))) + "."
                : string.Empty;
            var systemPrompt =
                $"You are Ino, the personal AI assistant of IntoChat OS (user: {settings.DisplayName}, model: {settings.DefaultModel}). "
                + (recalled.Length > 0 ? $"Recalled memory: {string.Join("; ", recalled.Select(r => r.Text))}. " : string.Empty)
                + chartContext
                + tableContext
                + " Answer concisely and accurately using the visible chart and table data when asked.";
            var messages = new List<AiMessage>
            {
                new("system", [new AiText(systemPrompt)]),
            };
            foreach (var turn in State.Turns.TakeLast(6))
            {
                messages.Add(new("user", [new AiText(turn.UserMessage)]));
                messages.Add(new("assistant", [new AiText(turn.Reply)]));
            }
            messages.Add(new("user", [new AiText(text)]));
            var result = await model.Generate(new InferenceRequest(messages));
            var generated = string.Join(
                "\n",
                result.Messages
                    .SelectMany(m => m.Content)
                    .OfType<AiText>()
                    .Select(t => t.Text)
                    .Where(t => !string.IsNullOrWhiteSpace(t))).Trim();
            return string.IsNullOrWhiteSpace(generated) ? null : generated;
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            Swallow(error, "Local model IQwen35 is optional when running InoApp in unit tests without Ollama.");
            return null;
        }
    }

    private IMemory? TryGetMemory()
    {
        try
        {
            return Get<IMemory>("memory");
        }
        catch (InvalidOperationException error)
        {
            Swallow(error, "MemoryNeuron is optional when running without AIModule registered.");
            return null;
        }
    }

    private IAppStudio? TryGetStudio()
    {
        try
        {
            return Get<IAppStudio>("main");
        }
        catch (InvalidOperationException error)
        {
            Swallow(error, "AppStudioNeuron is optional when running in unit tests without Creator registered.");
            return null;
        }
    }
}
