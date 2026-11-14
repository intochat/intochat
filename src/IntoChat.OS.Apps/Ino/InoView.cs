using System.Text.RegularExpressions;
using DigitalBrain.UI;
using IntoChat.OS.Apps.Markdraw;
using IntoChat.OS.Modules.Apps;
using IntoChat.OS.Modules.Apps.Approvals;

namespace IntoChat.OS.Apps.Ino;

public static partial class InoView
{
    public const string ConversationKey = "conversation";
    public const string ComposerKey = "composer";
    public const string ComposerField = "apps/ino/composer";
    public const string ProviderCredentialKey = "provider-credential";
    public const string ProviderCredentialField = "apps/ino/provider-credential";
    public const string UseLocalKey = "use-local-model";
    public const string StopKey = "stop";
    public const string RetryKey = "retry";
    public const string ErrorActionKey = "error-action";
    public const string NewKey = "new-conversation";
    public const int MaxRenderedMessages = 100;
    public const int MaxRenderedChars = 1_000_000;

    public static IReadOnlyList<InoMessage> Shown(InoAppState s)
    {
        ArgumentNullException.ThrowIfNull(s);
        var source = s.Messages ?? [];
        var selected = new List<InoMessage>(Math.Min(source.Length + 1, MaxRenderedMessages + 1));
        var totalChars = 0;

        for (var i = source.Length - 1; i >= 0 && selected.Count < MaxRenderedMessages; i--)
        {
            var msg = source[i];
            var text = msg.Text ?? string.Empty;
            if (selected.Count == 0 && text.Length > MaxRenderedChars)
            {
                selected.Add(msg with { Text = text[..MaxRenderedChars] });
                totalChars = MaxRenderedChars;
                break;
            }

            if (totalChars + text.Length > MaxRenderedChars)
            {
                break;
            }

            selected.Add(msg);
            totalChars += text.Length;
        }

        selected.Reverse();

        if (s.Pending is { } pending)
        {
            var pendingText = pending.Text.Length > MaxRenderedChars
                ? pending.Text[..MaxRenderedChars]
                : pending.Text;
            selected.Add(new InoMessage($"pending/{pending.Id}", "user", pendingText, "sending"));
        }

        return selected;
    }

    public static Conversation Conversation(InoAppState s)
    {
        ArgumentNullException.ThrowIfNull(s);
        var shown = Shown(s);
        var preserveScenarioVaultUri = string.Equals(s.ConversationId, "invoice-check", StringComparison.Ordinal);

        var projectedCardIds = new HashSet<string>(
            shown.SelectMany(m => m.Cards).Select(c => c.Id),
            StringComparer.Ordinal);
        var looseCardNodes = s.ActiveCards
            .Where(c => !projectedCardIds.Contains(c.Id))
            .SelectMany(c => RenderCardNodes(c, preserveScenarioVaultUri))
            .ToArray();

        var setupNodes = ShouldShowModelSetup(s) ? RenderModelSetupNodes(s) : [];

        var messageNodes = new List<MessageNode>(shown.Count);
        for (var i = 0; i < shown.Count; i++)
        {
            var msg = shown[i];
            var isLast = i == shown.Count - 1;
            var cardList = new List<Node>();
            foreach (var card in msg.Cards)
            {
                cardList.AddRange(RenderCardNodes(card, preserveScenarioVaultUri));
            }

            if (isLast)
            {
                if (looseCardNodes.Length > 0)
                {
                    cardList.AddRange(looseCardNodes);
                }

                if (setupNodes.Length > 0)
                {
                    cardList.AddRange(setupNodes);
                }
            }

            var activities = msg.Activities.Length > 0
                ? msg.Activities
                    .Select(a => new ActivityNode(
                        a.Id,
                        StripInternalToolLabel(a.Title),
                        Status: a.Error is { Length: > 0 } ? "failed" : "done",
                        Detail: StripNeuronUri(a.Error ?? a.Result)))
                    .ToArray()
                : null;

            Button? retry = isLast && msg.Role == "assistant" && msg.Status is "failed" or "interrupted"
                ? new Button(RetryKey, "Try again", Emit: AppSignals.Emits("ino", new InoRetry()))
                : null;

            messageNodes.Add(new MessageNode(
                msg.Id,
                msg.Role,
                msg.Text,
                Cards: cardList.Count > 0 ? cardList : null,
                Activities: activities,
                Status: msg.Status,
                Model: msg.Model,
                Retry: retry));
        }

        Node? waiting = null;
        if (s.Approvals is { Length: > 0 } approvals)
        {
            waiting = approvals.Length == 1
                ? RenderApproval(approvals[0])
                : new Column([.. approvals.Select(RenderApproval)]) { Gap = 8 };
        }
        else if (shown.Count == 0 && (setupNodes.Length > 0 || looseCardNodes.Length > 0))
        {
            var waitingItems = new List<Node>(looseCardNodes.Length + setupNodes.Length);
            waitingItems.AddRange(looseCardNodes);
            waitingItems.AddRange(setupNodes);
            waiting = waitingItems.Count == 1 ? waitingItems[0] : new Column(waitingItems) { Gap = 8 };
        }

        var errorLine = s.Error is { } err ? RenderErrorLine(err) : null;
        var stop = s.ActiveRunId is not null
            ? new Button(StopKey, "Stop", Emit: AppSignals.Emits("ino", new InoStop()))
            : null;
        var empty = shown.Count == 0
            ? new EmptyStateNode("What do you need?")
            : null;
        var composerEnabled = s.ActiveRunId is null && s.Pending is null && !s.Blocked;

        return new Conversation(
            ConversationKey,
            messageNodes,
            new ComposerNode(
                ComposerKey,
                AppSignals.Emits("ino", new InoSend()),
                "Ask anything.",
                Enabled: composerEnabled),
            Waiting: waiting,
            Status: ConversationStatusOf(s),
            Error: errorLine,
            Stop: stop,
            Empty: empty);
    }

    public static View Screen(InoAppState s)
    {
        ArgumentNullException.ThrowIfNull(s);
        var conversation = Conversation(s);
        if (string.Equals(s.ConversationId, "invoice-check", StringComparison.Ordinal) && s.ActiveCards.Length > 0)
        {
            return new View(
                "Ino",
                new Column([.. s.ActiveCards.SelectMany(c => RenderCardNodes(c, preserveScenarioVaultUri: true)), conversation]) { Gap = 12 });
        }

        return new View("Ino", conversation);
    }

    public static View Side(InoAppState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        var count = state.Messages is { Length: > 0 } msgs
            ? msgs.Count(m => m.Role == "user")
            : state.Turns.Length;
        return new View(
            "Chats",
            new Column(
            [
                new Surface(
                    [
                        new Row(
                        [
                            new TagPill("Ino · Arc Sidebar", "accent", "sparkles"),
                            new TagPill($"{count} turn(s)", "neutral"),
                        ]) { Gap = 6 },
                        new Text(state.Status) { Tone = "muted", Tabular = true },
                        new Button("new-session", "+ New Session", Emit: AppSignals.Emit("ino", "new"), Tone: ButtonTone.Accent) { Small = true, Icon = "plus" },
                    ],
                    Kind: SurfaceKind.Acrylic,
                    Title: state.Title),
                new Divider("SESSIONS"),
                Ui.NavItem($"session-{state.ConversationId}", state.Title, "sparkles", "open", selected: true, badge: $"{count}"),
                new Collection([new Entry(state.ConversationId, state.Title, $"{count} turn(s)")]),
                new Divider("WHAT INO MAY DO"),
                new Collection([.. (state.GrantSummaries ?? []).Select((summary, i) => new Entry($"grant-{i}", summary, null))]),
            ])
            { Gap = 8 });
    }

    private static string ConversationStatusOf(InoAppState s)
    {
        if (s.Blocked || string.Equals(s.Status, InoModel.Paused, StringComparison.Ordinal))
        {
            return "paused";
        }

        if (s.ActiveRunId is not null || s.Pending is not null || string.Equals(s.Status, InoModel.Working, StringComparison.Ordinal))
        {
            return "working";
        }

        if (s.Error is not null || string.Equals(s.Status, InoModel.Failed, StringComparison.Ordinal))
        {
            return "failed";
        }

        return "ready";
    }

    private static string StripInternalToolLabel(string? title)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            return "Tool call";
        }

        var clean = StripNeuronUri(title);
        if (clean.Contains("IntoChat.", StringComparison.Ordinal))
        {
            var lastDot = clean.LastIndexOf('.');
            clean = lastDot >= 0 && lastDot + 1 < clean.Length ? clean[(lastDot + 1)..] : "Tool call";
        }

        return clean;
    }

    internal static bool ShouldShowModelSetup(InoAppState s)
        => s.Error?.Code is "no-provider" or "provider-rejected-key";

    internal static Node[] RenderModelSetupNodes(InoAppState s)
    {
        var options = new List<ChoiceOptionNode>();
        var localModels = s.DetectedLocalModels is { Length: > 0 } list
            ? list
            : s.DetectedLocalModel is { Length: > 0 } single
                ? [single]
                : [];

        foreach (var model in localModels.Take(3))
        {
            options.Add(new ChoiceOptionNode($"Local Ollama · {model}", "Ready on this machine"));
        }

        options.Add(new ChoiceOptionNode("OpenAI", "Cloud API key in Secure store"));
        options.Add(new ChoiceOptionNode("Anthropic", "Cloud API key in Secure store"));
        options.Add(new ChoiceOptionNode("Google Gemini", "Cloud API key in Secure store"));
        options.Add(new ChoiceOptionNode("OpenRouter", "Cloud API key in Secure store"));

        var children = new List<Node>();
        if (s.DetectedLocalModel is { Length: > 0 } preferred)
        {
            children.Add(new Button(
                UseLocalKey,
                $"Use local model ({preferred})",
                Emit: AppSignals.Emits("ino", new InoUseLocalModel(Model: preferred)),
                Tone: ButtonTone.Accent)
            { Icon = "cpu" });
        }

        children.Add(new Field(
            ProviderCredentialKey,
            ProviderCredentialField,
            FieldKind.Password,
            "Paste API key and press Enter to save",
            OnSubmit: AppSignals.Emits("ino", new InoSaveProvider())));

        return
        [
            new ChoiceCardNode(
                "model-setup-choice",
                "Choose a model source",
                options),
            new Card(
                "Connect a model",
                s.DetectedLocalModel is { Length: > 0 } local
                    ? $"Local Ollama model {local} was detected on this machine, or paste a provider API key below."
                    : "Start local Ollama or paste an OpenAI, Anthropic, Google, or OpenRouter API key below.",
                children),
        ];
    }

    internal static Button? BuildErrorAction(InoErrorLine line)
    {
        if (line.ActionLabel is null)
        {
            return null;
        }

        if (line.Retry)
        {
            return new Button(ErrorActionKey, line.ActionLabel, Emit: AppSignals.Emits("ino", new InoRetry()));
        }

        if (line.Open is { Length: > 0 } open)
        {
            return new Button(ErrorActionKey, line.ActionLabel, Intent: AppSignals.Open(open));
        }

        return null;
    }

    private static ErrorLineNode RenderErrorLine(InoError error)
    {
        var line = InoErrors.Of(error);
        return new ErrorLineNode(line.What, line.Detail, BuildErrorAction(line));
    }

    internal static Node RenderApproval(Approval approval) => new ApprovalCardNode(
        $"approval-{approval.PlanId}",
        approval.PlanId,
        approval.Title,
        approval.Detail,
        [new ApprovalRowNode("Subject", "", approval.Subject)],
        SystemName: "Ino",
        Status: "pending",
        Resolve: new Resolve(approval.PlanId));

    public static string StripNeuronUri(string? uri)
    {
        if (string.IsNullOrWhiteSpace(uri)) { return string.Empty; }
        var text = uri.Trim();
        if (text.StartsWith("neuron://", StringComparison.OrdinalIgnoreCase))
        {
            var afterScheme = text["neuron://".Length..];
            var slash = afterScheme.IndexOf('/');
            text = slash >= 0 && slash + 1 < afterScheme.Length
                ? afterScheme[(slash + 1)..]
                : afterScheme;
        }
        text = PipeScopeRegex().Replace(text, "");
        text = ParenScopeRegex().Replace(text, "");
        text = BareScopeRegex().Replace(text, "");
        return text.Trim();
    }

    [GeneratedRegex(@"\s*\|\s*scope:[^\s\)]*", RegexOptions.IgnoreCase)]
    private static partial Regex PipeScopeRegex();

    [GeneratedRegex(@"\s*\(scope:[^\)]*\)", RegexOptions.IgnoreCase)]
    private static partial Regex ParenScopeRegex();

    [GeneratedRegex(@"scope:[^\s\)]*", RegexOptions.IgnoreCase)]
    private static partial Regex BareScopeRegex();

    private static string ColumnOr(InoCard card, int index, string fallback) =>
        card.Columns is { } cols && cols.Length > index && !string.IsNullOrWhiteSpace(cols[index]) ? cols[index] : fallback;

    internal static IEnumerable<Node> RenderCardNodes(InoCard card, bool preserveScenarioVaultUri = false)
    {
        switch (card.Kind)
        {
            case "approval":
                yield return new ApprovalCardNode(
                    $"card-{card.Id}",
                    card.Id,
                    card.Title,
                    card.Summary,
                    [
                        new ApprovalRowNode("Status", "pending", card.Status),
                        new ApprovalRowNode("Source", "local", StripNeuronUri(card.SourceNeuronUri)),
                        new ApprovalRowNode("Scope", "ino", StripNeuronUri(card.EffectiveScopeUri)),
                    ],
                    SystemName: "Ino",
                    Status: card.Status,
                    SourceNeuronUri: StripNeuronUri(card.SourceNeuronUri));
                break;
            case "choice":
                yield return new ChoiceCardNode(
                    card.Id,
                    card.Title,
                    [
                        new ChoiceOptionNode("Primary Workspace", "circles/enterprise"),
                        new ChoiceOptionNode("Executive Board Circle", "circles/board"),
                    ],
                    PickedIndex: card.Status == "approved" ? 0 : null);
                break;
            case "draft":
                yield return new DraftCardNode(
                    card.Id,
                    "email",
                    "user@intochat.os",
                    "team@intochat.os",
                    card.Summary,
                    Subject: card.Title,
                    Status: card.Status == "approved" ? "sent" : card.Status);
                break;
            case "secret":
                var savedSource = preserveScenarioVaultUri
                    ? card.SourceNeuronUri
                    : StripNeuronUri(card.SourceNeuronUri);
                yield return new SecretCardNode(
                    card.Title,
                    card.Summary,
                    SavedIn: string.IsNullOrWhiteSpace(savedSource) ? "Secure Vault" : $"Secure Vault ({savedSource})",
                    Status: card.Status == "approved" ? "saved" : card.Status,
                    Enter: new Button("enter-secret", "Save to vault", Intent: AppSignals.Open("os://window/secure")));
                break;
            case "app-install":
                yield return new RecordCardNode(
                    "Smart App Manifest",
                    card.Title,
                    [
                        new RecordFieldNode("Publisher", "IntoChat OS (Verified)"),
                        new RecordFieldNode("Capabilities", "Files (read), Postgres (read)"),
                        new RecordFieldNode("Status", card.Status),
                    ],
                    Subtitle: card.Summary,
                    LinkLabel: "Review & Install",
                    Open: new Button("review-install", "Review & Install", Intent: AppSignals.Open("os://sheet/install")));
                break;
            case "table" when card.Columns is { Length: > 0 } cols && card.Rows is { Length: > 0 } rows:
                yield return new DataTable(
                    card.Title,
                    cols,
                    rows.Select(r => (IReadOnlyList<string>)r).ToArray(),
                    SourceNeuronUri: StripNeuronUri(card.SourceNeuronUri),
                    From: "Postgres");
                break;
            case "chart" when card.ChartPoints is { Length: > 0 } points:
                yield return new Chart(
                    card.ChartKind ?? "bar",
                    card.Title,
                    points.Select(pt => new ChartPointNode(pt.Label, pt.Value)).ToArray(),
                    SeriesName: "Current",
                    SourceNeuronUri: StripNeuronUri(card.SourceNeuronUri),
                    From: "Postgres",
                    Prefix: "€");
                break;
            case "markdraw":
                var activeDoc = ColumnOr(card, 0, "/work/architecture.markdraw");
                var format = ColumnOr(card, 1, "markdraw");
                var rev = int.TryParse(ColumnOr(card, 2, "1"), out var parsedRev) ? parsedRev : 1;
                var content = ColumnOr(card, 3, MarkdrawApp.DefaultArchitectureScene);
                var exportPath = card.Columns is { Length: >= 5 } && !string.IsNullOrWhiteSpace(card.Columns[4]) ? card.Columns[4] : null;
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
                break;
            default:
                if (IntoChat.OS.Modules.ArtCraft.ArtCraftCatalog.TryGet(card.Kind, out var studioDescriptor))
                {
                    yield return new RecordCardNode(
                        $"{studioDescriptor.Title} · {studioDescriptor.Subtitle}",
                        card.Title,
                        [
                            new RecordFieldNode("Status", card.Status),
                            new RecordFieldNode("Studio", StripNeuronUri(card.SourceNeuronUri)),
                            new RecordFieldNode("Scope", StripNeuronUri(card.EffectiveScopeUri)),
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
                            new RecordFieldNode("Source", StripNeuronUri(card.SourceNeuronUri)),
                            new RecordFieldNode("Scope", StripNeuronUri(card.EffectiveScopeUri)),
                        ],
                        Subtitle: card.Summary,
                        LinkLabel: card.Kind == "file" ? "Open in Files" : "Open",
                        Open: card.Kind == "file" ? new Button("open-files", "Open in Files", Intent: AppSignals.OpenApp("files")) : null);
                }
                break;
        }
    }

    internal static InoAppState BuildScenarioSessionState(string scenarioId, UserSettingsState settings) => scenarioId switch
    {
        "morning-brief" => new(
            "morning-brief",
            "Morning Brief",
            $"Scenario: Morning Brief ({settings.DefaultModel})",
            "idle",
            [new InoTurn("Prepare my Morning Brief", "Good morning! You have **3 meetings**, **2 Q4 renewals**, and **1 pending review**.", "scenario:morning-brief", 2)],
            [new InoCard("choice-brief", "choice", "Which workspace should receive the Q4 renewal brief?", "Select a target Circle (1..2) or press Enter.", "neuron://local/circles/primary", "pending", "neuron://local/apps/ino#choice-brief")]),
        "invoice-check" => new(
            "invoice-check",
            "Tripletex Invoice Check",
            "Waiting on Secure Vault credential",
            "idle",
            [new InoTurn("Check overdue Q4 invoices in Tripletex", "I need your **Tripletex Session Token** to read the Q4 ledger. Enter it in the **Secure Vault** window so it never touches the LLM.", "vault:secret", 0)],
            [new InoCard("secret-tripletex", "secret", "Tripletex Session Token", "Zero-leakage credential request · Sealed in Secure Vault", "neuron://local/vault/secrets#TRIPLETEX_SESSION_TOKEN", "pending", "neuron://local/apps/ino#secret-tripletex")]),
        "first-run" => new(
            "first-run",
            "Ino Setup · First Run",
            "Guided onboarding · 3 steps",
            "idle",
            [new InoTurn("/scenario first-run", $"Welcome to **IntoChat OS**! Let's verify your default AI model (`{settings.DefaultModel}`), connect your **Files** vault, and seal API keys in **Secure**.", "onboarding:setup", 0)],
            [new InoCard("setup-record", "record", "IntoChat OS First-Run Checklist", "All 3 local brain capabilities online", "neuron://local/os.settings/settings", "ready", "neuron://local/apps/ino#setup-record")]),
        _ => new(
            "q4-renewal",
            "Quarterly Enterprise Renewal",
            "Waiting for approval · CloseDate -> 15 Nov 2026",
            "idle",
            [new InoTurn("Update quarterly enterprise renewal close date to 15 Nov 2026", "Confirmed moving the **Quarterly Enterprise** renewal to **15 Nov 2026**. Please approve the CRM update below.", $"agent:{settings.DefaultModel}", 1)],
            [
                new InoCard("card-1", "approval", "Update close date on quarterly renewal to 15 Nov 2026?", "Ino matched the incoming email to your open CRM opportunity.", "neuron://local/os.confirmations/inbox", "pending", "neuron://local/apps/ino#card-1"),
                new InoCard("draft-1", "draft", "Quarterly Enterprise Renewal — 15 Nov confirmation", "Confirmed: we updated the Q4 renewal close date to 15 Nov 2026.", "neuron://local/apps/ino#draft-1", "pending", "neuron://local/apps/ino#draft-1"),
            ]),
    };
}
