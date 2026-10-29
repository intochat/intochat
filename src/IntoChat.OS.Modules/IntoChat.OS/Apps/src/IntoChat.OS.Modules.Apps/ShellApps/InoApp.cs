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

public sealed record InoCard(
    string Id,
    string Kind,
    string Title,
    string Summary,
    string SourceNeuronUri,
    string Status,
    string? SurfaceScopeUri = null,
    string[]? Columns = null,
    string[][]? Rows = null)
{
    public string EffectiveScopeUri => SurfaceScopeUri ?? $"neuron://local/apps/ino#{Id}";
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
    string? ConnectedDatabase = null)
{
    public InoAppState() : this("main", "New chat", "Ready", "idle", [], [], null) { }

    public InoCard[] ActiveCards => Cards ?? [];
}

[DigitalBrain.App("ino")]
public sealed class InoApp : DigitalBrain.App<InoAppState>
{
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

        if (LooksLikeConnectionString(text))
        {
            await ConnectDb(new InoConnectDb(text, "main"));
            return;
        }

        if (LooksLikeDatabaseQuery(text))
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
        var generated = string.IsNullOrWhiteSpace(request.EffectiveMessage)
            ? null
            : await TryGenerateReplyAsync(text, settings, recalled);
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

        Set(State with
        {
            Title = title,
            Status = routeHint,
            Turns = turns,
            Cards = cards,
        });
    }

    [DigitalBrain.On]
    public async Task ConnectDb(InoConnectDb request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var raw = string.IsNullOrWhiteSpace(request.ConnectionString)
            ? "Host=localhost;Port=5432;Database=nordlys;Username=postgres"
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
            $"Connection sealed in neuron://local/postgres/{alias}. Ready for read-only SQL and natural-language queries.",
            $"neuron://local/postgres/{alias}",
            "connected",
            $"neuron://local/apps/ino#{cardId}");
        var turns = State.Turns.Append(new InoTurn(
            $"Connect database ({hostName}/{dbName})",
            $"Connected to PostgreSQL database '{dbName}' on '{hostName}' (neuron://local/postgres/{alias}). Ask me to query any table or run a SELECT statement.",
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

        if (string.IsNullOrWhiteSpace(State.ConnectedDatabase))
        {
            var secretId = $"secret-{State.ActiveCards.Length + 1}";
            var secretCard = new InoCard(
                secretId,
                "secret",
                "PostgreSQL Connection String Required",
                "Provide a PostgreSQL connection string (e.g. Host=localhost;Database=nordlys;Username=postgres) so Ino can query neuron://local/postgres/main.",
                "neuron://local/os.secrets/postgres",
                "waiting",
                $"neuron://local/apps/ino#{secretId}");
            var promptTurns = State.Turns.Append(new InoTurn(
                sql,
                "I need a PostgreSQL connection string before I can run that query. Paste `Host=...;Database=...` or enter it in the secure credential prompt.",
                "postgres:awaiting-credential",
                0)).ToArray();
            Set(State with
            {
                Status = "Waiting for PostgreSQL connection string",
                Turns = promptTurns,
                Cards = [.. State.ActiveCards, secretCard],
            });
            return;
        }

        var (columns, rows, summary) = ExecuteDatasetQuery(sql, State.ConnectedDatabase);
        if (TryGetMemory() is { } memory)
        {
            await memory.CaptureEpisode("ino.query", $"SQL: {sql} | Rows: {rows.Length}", importance: 6);
        }

        var tableCardId = $"table-{State.ActiveCards.Length + 1}";
        var tableCard = new InoCard(
            tableCardId,
            "table",
            $"Query Result ({rows.Length} rows from {State.ConnectedDatabase})",
            summary,
            "neuron://local/postgres/main#query-result",
            "ready",
            $"neuron://local/apps/ino#{tableCardId}",
            columns,
            rows);
        var nextTurns = State.Turns.Append(new InoTurn(
            sql,
            $"{summary} Rendered {rows.Length} row(s) from neuron://local/postgres/main.",
            "postgres:query",
            0)).ToArray();

        Set(State with
        {
            Status = $"Queried {rows.Length} row(s) from {State.ConnectedDatabase}",
            Turns = nextTurns,
            Cards = [.. State.ActiveCards, tableCard],
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
        new Collection([new Entry(State.ConversationId, State.Title, $"{State.Turns.Length} turn(s)")]));

    public View Render() => new(
        "Ino",
        new Column(
        [
            new Text(State.Status),
            .. State.ActiveCards.SelectMany(RenderCardNodes),
            new Collection(State.Turns.Select((turn, i) =>
                new Entry($"turn-{i + 1}", turn.UserMessage, turn.Reply)).ToArray()),
        ]));

    private static IEnumerable<Node> RenderCardNodes(InoCard card)
    {
        yield return new Text($"[{card.Kind}:{card.Status}] {card.Title} ({card.SourceNeuronUri} | scope:{card.EffectiveScopeUri})");
        var children = new List<Node>();
        if (card.Kind == "approval" && card.Status == "pending")
        {
            children.Add(new Button(
                "Approve",
                new ButtonAction("apps/ino", "approve-card", [card.Id]),
                Enabled: true,
                Tone: ButtonTone.Accent));
        }
        else if (card.Kind == "table" && card.Columns is { Length: > 0 } cols && card.Rows is { Length: > 0 } rows)
        {
            children.Add(new Collection(rows.Select((row, r) =>
                new Entry(
                    $"{card.Id}-row-{r + 1}",
                    row[0],
                    string.Join(" · ", cols.Skip(1).Zip(row.Skip(1), (col, val) => $"{col}: {val}")))).ToArray()));
        }
        yield return new Card(card.Title, card.Summary, children);
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
        if (!State.ActiveCards.Any(c => c.Kind == "table" && c.Rows is { Length: > 0 }))
        {
            throw new InvalidOperationException("Expected InoApp to execute SQL query after receiving connection string and emit a table card.");
        }

        var snapshot = await memory.Read();
        if (snapshot.Episodes.Length == 0)
        {
            throw new InvalidOperationException("Expected InoApp to capture the conversation episode in MemoryNeuron.");
        }
    }

    private static bool LooksLikeConnectionString(string text)
        => text.Contains("Host=", StringComparison.OrdinalIgnoreCase)
            || text.Contains("Server=", StringComparison.OrdinalIgnoreCase)
            || text.StartsWith("postgres://", StringComparison.OrdinalIgnoreCase)
            || text.StartsWith("postgresql://", StringComparison.OrdinalIgnoreCase);

    private static bool LooksLikeDatabaseQuery(string text)
        => text.StartsWith("SELECT ", StringComparison.OrdinalIgnoreCase)
            || text.Contains("query database", StringComparison.OrdinalIgnoreCase)
            || text.Contains("from postgres", StringComparison.OrdinalIgnoreCase)
            || text.Contains("from opportunities", StringComparison.OrdinalIgnoreCase);

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

    private static (string[] Columns, string[][] Rows, string Summary) ExecuteDatasetQuery(string sql, string database)
    {
        if (sql.Contains("customer", StringComparison.OrdinalIgnoreCase) || sql.Contains("account", StringComparison.OrdinalIgnoreCase) && !sql.Contains("opportunit", StringComparison.OrdinalIgnoreCase))
        {
            string[] cols = ["Account", "City", "Owner", "AnnualRevenue"];
            string[][] rows =
            [
                ["Acme Logistics", "Oslo", "Maja Berg", "€312,000"],
                ["Havbris AS", "Bergen", "Jeff Tilley", "€240,000"],
                ["Fjordline", "Stavanger", "Erik Nilsen", "€192,000"],
                ["Nordkapp Shipping", "Tromsø", "Maja Berg", "€148,500"],
            ];
            return (cols, rows, $"Queried 4 customer account(s) from {database}.");
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
        return (defaultCols, defaultRows, $"Queried 5 opportunity row(s) totaling €627,700 from {database}.");
    }

    private async Task<string?> TryGenerateReplyAsync(string text, UserSettingsState settings, MemoryRecallHit[] recalled)
    {
        try
        {
            var model = Get<IQwen35>("main");
            var systemPrompt =
                $"You are Ino, the personal AI assistant of IntoChat OS (user: {settings.DisplayName}, model: {settings.DefaultModel}). "
                + (recalled.Length > 0 ? $"Recalled memory: {string.Join("; ", recalled.Select(r => r.Text))}. " : string.Empty)
                + "Reply concisely and helpfully.";
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
