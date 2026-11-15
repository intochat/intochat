using System.Text;
using System.Text.RegularExpressions;
using DigitalBrain.UI;
using IntoChat.OS.Apps.Markdraw;
using IntoChat.OS.Framework.Visuals;
using IntoChat.OS.Modules.Apps;
using IntoChat.OS.Modules.Apps.Approvals;
using IntoChat.OS.UI;
using static IntoChat.OS.UI.Kit;

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
                break;
            }
            if (totalChars + text.Length > MaxRenderedChars) { break; }
            selected.Add(msg);
            totalChars += text.Length;
        }

        selected.Reverse();
        if (s.Pending is { } pending)
        {
            var pendingText = pending.Text.Length > MaxRenderedChars ? pending.Text[..MaxRenderedChars] : pending.Text;
            selected.Add(new InoMessage($"pending/{pending.Id}", "user", pendingText, "sending"));
        }

        return selected;
    }

    public static Conversation Conversation(InoAppState s)
    {
        ArgumentNullException.ThrowIfNull(s);
        var shown = Shown(s);
        var projectedCardIds = new HashSet<string>(shown.SelectMany(m => m.Cards).Select(c => c.Id), StringComparer.Ordinal);
        var looseCardNodes = s.ActiveCards.Where(c => !projectedCardIds.Contains(c.Id)).SelectMany(RenderCardNodes).ToArray();
        var setupNodes = ShouldShowModelSetup(s) ? RenderModelSetupNodes(s) : [];

        var messageNodes = new List<MessageNode>(shown.Count);
        for (var i = 0; i < shown.Count; i++)
        {
            var msg = shown[i];
            var isLast = i == shown.Count - 1;
            var (displayText, inlineSlots) = msg.Role == "assistant"
                ? InoCards.ExtractInlineSlots(msg.Text)
                : (msg.Text, Array.Empty<NeuronRef>());

            var cardList = new List<Node>();
            foreach (var card in msg.Cards) { cardList.AddRange(RenderCardNodes(card)); }
            foreach (var slot in inlineSlots) { cardList.Add(slot); }
            if (isLast)
            {
                cardList.AddRange(looseCardNodes);
                cardList.AddRange(setupNodes);
            }

            var activities = msg.Activities.Length > 0 ? msg.Activities.Select(ToActivityNode).ToArray() : null;
            var errorCarriesRetry = s.Error is { } activeErr && InoErrors.Of(activeErr).Retry;
            Button? retry = isLast && msg.Role == "assistant" && msg.Status is "failed" or "interrupted" && !errorCarriesRetry
                ? Button("Try again", Icons.RefreshCw).Small().Key(RetryKey).Emits(AppSignals.Emits("ino", new InoRetry())).ToButton()
                : null;
            var modelVisual = ResolveModelVisual(msg.Model);

            messageNodes.Add(new MessageNode(
                msg.Id,
                msg.Role,
                displayText,
                Cards: cardList.Count > 0 ? cardList : null,
                Activities: activities,
                Status: msg.Status,
                Model: modelVisual?.Label ?? msg.Model,
                Retry: retry,
                ModelLabel: modelVisual?.Label,
                ProviderIcon: modelVisual?.IconKey));
        }

        var waiting = BuildDockedWaitingNode(s, shown.Count, looseCardNodes, setupNodes);
        var errorLine = s.Error is { } err ? RenderErrorLine(err) : null;
        Button? stop = s.ActiveRunId is not null
            ? GhostButton("Stop").Small().Key(StopKey).Emits(AppSignals.Emits("ino", new InoStop())).ToButton()
            : null;
        var activeModelVisual = ResolveModelVisual(s.SelectedModel);

        return new Conversation(
            ConversationKey,
            messageNodes,
            new ComposerNode(
                ComposerKey,
                AppSignals.Emits("ino", new InoSend()),
                "Ask anything.",
                Enabled: s.ActiveRunId is null && s.Pending is null && !s.Blocked,
                ActiveModelLabel: activeModelVisual?.Label,
                ActiveProviderIcon: activeModelVisual?.IconKey),
            Waiting: waiting,
            Status: ConversationStatusOf(s),
            Error: errorLine,
            Stop: stop,
            Empty: shown.Count == 0 ? Empty("What do you need?") : null);
    }

    public static View Screen(InoAppState s)
    {
        ArgumentNullException.ThrowIfNull(s);
        var conversation = Conversation(s);
        if (HasUnprojectedVaultSecretPrompt(s))
        {
            Node[] topNodes = [.. s.ActiveCards.SelectMany(RenderCardNodes), conversation];
            return new View("Ino", VStack(Space.Md, topNodes));
        }

        return new View("Ino", conversation);
    }

    public static View Side(InoAppState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        var count = state.Messages is { Length: > 0 } msgs ? msgs.Count(m => m.Role == "user") : state.Turns.Length;
        var (statusKind, statusLabel) = ResolveSideStatus(state);
        var activeModel = ResolveModelVisual(state.SelectedModel)?.Label ?? state.SelectedModel;

        return new View(
            "Chats",
            VStack(Space.Sm)[
                Surface(SurfaceKind.Acrylic)[
                    VStack(Space.Sm)[
                        HeaderBar(state.Title, activeModel, StatusPill(statusKind, statusLabel)),
                        MutedText(state.Status).Tabular(),
                        PrimaryButton("+ New Session", Icons.Plus).Small().Key("new-session").Emits(AppSignals.Emit("ino", "new")).ToNode()
                    ]
                ],
                Divider("SESSIONS"),
                NavItem($"session-{state.ConversationId}", state.Title, Icons.Sparkles, "open", selected: true, badge: $"{count}"),
                new Collection([new Entry(state.ConversationId, state.Title, $"{count} turn(s)")]),
                Divider("WHAT INO MAY DO"),
                new Collection([.. (state.GrantSummaries ?? []).Select((summary, i) => new Entry($"grant-{i}", summary, null))])
            ]);
    }

    internal static (StatusKind Kind, string Label) ResolveSideStatus(InoAppState s) =>
        (s.Approvals is { Length: > 0 }) || s.ActiveCards.Any(static c => c.IsWaiting) || s.Blocked || string.Equals(s.Status, InoModel.Paused, StringComparison.Ordinal)
            ? (StatusKind.NeedsYou, s.Blocked ? "Paused" : "Needs you")
        : s.ActiveRunId is not null || s.Pending is not null || string.Equals(s.Status, InoModel.Working, StringComparison.Ordinal)
            ? (StatusKind.Working, "Working")
        : s.Error is not null || string.Equals(s.Status, InoModel.Failed, StringComparison.Ordinal)
            ? (StatusKind.Error, "Attention")
        : (StatusKind.Done, "Ready");

    internal static bool IsVaultSecretUri(string? uri)
        => !string.IsNullOrWhiteSpace(uri) && uri.Contains("/vault/secrets", StringComparison.OrdinalIgnoreCase);

    internal static bool HasUnprojectedVaultSecretPrompt(InoAppState s)
        => (s.Messages is null or { Length: 0 })
            && s.Pending is null
            && s.ActiveCards.Any(static c => string.Equals(c.Kind, "secret", StringComparison.Ordinal) && IsVaultSecretUri(c.SourceNeuronUri));

    private static Node? BuildDockedWaitingNode(InoAppState s, int shownCount, Node[] looseCardNodes, Node[] setupNodes)
    {
        if (s.Approvals is { Length: > 0 } approvals)
        {
            return approvals.Length == 1 ? RenderApproval(approvals[0]) : VStack(Space.Sm, [.. approvals.Select(RenderApproval)]);
        }

        if (shownCount == 0 && (setupNodes.Length > 0 || looseCardNodes.Length > 0))
        {
            Node[] items = [.. looseCardNodes, .. setupNodes];
            return items.Length == 1 ? items[0] : VStack(Space.Sm, items);
        }

        return null;
    }

    private static ActivityNode ToActivityNode(InoActivity a)
    {
        var visual = ResolveActivityVisual(a);
        return new ActivityNode(a.Id, visual.Label, a.Error is { Length: > 0 } ? "failed" : "done", StripNeuronUri(a.Error ?? a.Result), visual.IconKey, visual.Category);
    }

    private static string ConversationStatusOf(InoAppState s)
        => s.Blocked || string.Equals(s.Status, InoModel.Paused, StringComparison.Ordinal) ? "paused"
            : s.ActiveRunId is not null || s.Pending is not null || string.Equals(s.Status, InoModel.Working, StringComparison.Ordinal) ? "working"
            : s.Error is not null || string.Equals(s.Status, InoModel.Failed, StringComparison.Ordinal) ? "failed"
            : "ready";

    public static VisualDescriptor? ResolveModelVisual(string? model)
        => string.IsNullOrWhiteSpace(model) ? null : VisualCatalog.ForModel(model);

    public static VisualDescriptor ResolveApprovalVisual(string? sourceOrSubject)
        => VisualCatalog.TryForContract(sourceOrSubject, out var found) ? found
            : !string.IsNullOrWhiteSpace(sourceOrSubject) && sourceOrSubject.Trim().StartsWith("change-", StringComparison.OrdinalIgnoreCase)
                ? VisualCatalog.ForContract("IGitHub")
                : VisualCatalog.ForContract("IConfirmations");

    public static VisualDescriptor ResolveActivityVisual(InoActivity activity)
    {
        ArgumentNullException.ThrowIfNull(activity);
        if (string.IsNullOrWhiteSpace(activity.Title))
        {
            return new VisualDescriptor("Tool call", "capability:studio", "#64748B", "Tool");
        }

        var clean = StripNeuronUri(activity.Title);
        if (clean.IndexOf(" · ", StringComparison.Ordinal) is > 0 and var sep)
        {
            var head = clean[..sep].Trim();
            var tail = clean[(sep + 3)..].Trim();
            if (VisualCatalog.TryForContract(head, out var preformatted))
            {
                return string.IsNullOrWhiteSpace(tail) ? preformatted : preformatted with { Label = $"{preformatted.Label} · {tail}" };
            }
        }

        if (VisualCatalog.TryForContract(clean, out var direct))
        {
            var action = ExtractMethodSuffix(clean, direct.Label);
            return string.IsNullOrWhiteSpace(action) ? direct : direct with { Label = $"{direct.Label} · {action}" };
        }

        return clean switch
        {
            "neurons_search" => new VisualDescriptor("Capability Search", "capability:studio", "#6366F1", "Studio"),
            "neurons_list" => new VisualDescriptor("Capability List", "capability:studio", "#6366F1", "Studio"),
            "neurons_context" => new VisualDescriptor("Brain Context", "brand:intochat", "#6366F1", "Studio"),
            _ => new VisualDescriptor(StripInternalToolLabel(clean), "capability:studio", "#64748B", "Tool"),
        };
    }

    private static string? ExtractMethodSuffix(string rawTitle, string resolvedLabel)
    {
        var lastDot = rawTitle.LastIndexOf('.');
        if (lastDot < 0 || lastDot + 1 >= rawTitle.Length) { return null; }
        var candidate = rawTitle[(lastDot + 1)..].Trim();
        if (!candidate.Any(char.IsUpper) || VisualCatalog.TryForContract(candidate, out _)) { return null; }
        if (candidate.EndsWith("Async", StringComparison.Ordinal) && candidate.Length > 5) { candidate = candidate[..^5]; }
        if (candidate.StartsWith("Get", StringComparison.Ordinal) && candidate.Length > 3 && char.IsUpper(candidate[3])) { candidate = candidate[3..]; }
        var humanized = HumanizeMethodName(candidate);
        return string.IsNullOrWhiteSpace(humanized) || string.Equals(humanized, resolvedLabel, StringComparison.OrdinalIgnoreCase) ? null : humanized;
    }

    private static string HumanizeMethodName(string method)
    {
        var sb = new StringBuilder(method.Length + 4);
        for (var i = 0; i < method.Length; i++)
        {
            var c = method[i];
            if (i > 0 && char.IsUpper(c) && (char.IsLower(method[i - 1]) || char.IsDigit(method[i - 1]))) { sb.Append(' '); }
            sb.Append(c);
        }

        return sb.ToString().Trim();
    }

    private static string StripInternalToolLabel(string? title)
    {
        if (string.IsNullOrWhiteSpace(title)) { return "Tool call"; }
        var clean = StripNeuronUri(title);
        return clean.Contains("IntoChat.", StringComparison.Ordinal) && clean.LastIndexOf('.') is >= 0 and var dot && dot + 1 < clean.Length
            ? clean[(dot + 1)..]
            : clean.Contains("IntoChat.", StringComparison.Ordinal) ? "Tool call" : clean;
    }

    internal static bool ShouldShowModelSetup(InoAppState s) => s.Error?.Code is "no-provider" or "provider-rejected-key";

    internal static Node[] RenderModelSetupNodes(InoAppState s)
    {
        var localModels = s.DetectedLocalModels is { Length: > 0 } list ? list : s.DetectedLocalModel is { Length: > 0 } single ? [single] : [];
        ChoiceOptionNode[] options =
        [
            .. localModels.Take(3).Select(static m => new ChoiceOptionNode($"Local Ollama · {m}", "Ready on this machine")),
            new("OpenAI", "Cloud API key in Secure store"),
            new("Anthropic", "Cloud API key in Secure store"),
            new("Google Gemini", "Cloud API key in Secure store"),
            new("OpenRouter", "Cloud API key in Secure store"),
        ];

        var subtitle = s.DetectedLocalModel is { Length: > 0 } local
            ? $"Local Ollama model {local} was detected on this machine, or paste a provider API key below."
            : "Start local Ollama or paste an OpenAI, Anthropic, Google, or OpenRouter API key below.";
        Node? useLocal = s.DetectedLocalModel is { Length: > 0 } preferred
            ? PrimaryButton($"Use local model ({preferred})", "cpu").Key(UseLocalKey).Emits(AppSignals.Emits("ino", new InoUseLocalModel(Model: preferred))).ToNode()
            : null;

        return
        [
            new ChoiceCardNode("model-setup-choice", "Choose a model source", options),
            Card("Connect a model", subtitle)[
                useLocal,
                SecretField(ProviderCredentialField, "Paste API key and press Enter to save").Key(ProviderCredentialKey).Emits(AppSignals.Emits("ino", new InoSaveProvider()))
            ],
        ];
    }

    internal static Button? BuildErrorAction(InoErrorLine line)
        => line.ActionLabel is null ? null
            : line.Retry ? Button(line.ActionLabel, Icons.RefreshCw).Small().Key(ErrorActionKey).Emits(AppSignals.Emits("ino", new InoRetry())).ToButton()
            : line.Open is { Length: > 0 } open ? Button(line.ActionLabel).Small().Key(ErrorActionKey).WithIntent(IntentKind.OpenWindow, open).ToButton()
            : null;

    private static ErrorLineNode RenderErrorLine(InoError error)
    {
        var line = InoErrors.Of(error);
        return new ErrorLineNode(line.What, line.Detail, BuildErrorAction(line));
    }

    internal static Node RenderApproval(Approval approval)
    {
        var visual = ResolveApprovalVisual(approval.Subject);
        return new ApprovalCardNode(
            $"approval-{approval.PlanId}", approval.PlanId, approval.Title, approval.Detail,
            [new ApprovalRowNode("Subject", "", approval.Subject)],
            SystemName: "Ino", Status: "pending", Resolve: new Resolve(approval.PlanId),
            Icon: visual.IconKey, CapabilityLabel: visual.Label);
    }

    public static string StripNeuronUri(string? uri)
    {
        if (string.IsNullOrWhiteSpace(uri)) { return string.Empty; }
        var text = uri.Trim();
        if (text.StartsWith("neuron://", StringComparison.OrdinalIgnoreCase))
        {
            var afterScheme = text["neuron://".Length..];
            var slash = afterScheme.IndexOf('/');
            text = slash >= 0 && slash + 1 < afterScheme.Length ? afterScheme[(slash + 1)..] : afterScheme;
        }

        return BareScopeRegex().Replace(ParenScopeRegex().Replace(PipeScopeRegex().Replace(text, ""), ""), "").Trim();
    }

    [GeneratedRegex(@"\s*\|\s*scope:[^\s\)]*", RegexOptions.IgnoreCase)]
    private static partial Regex PipeScopeRegex();

    [GeneratedRegex(@"\s*\(scope:[^\)]*\)", RegexOptions.IgnoreCase)]
    private static partial Regex ParenScopeRegex();

    [GeneratedRegex(@"scope:[^\s\)]*", RegexOptions.IgnoreCase)]
    private static partial Regex BareScopeRegex();

    private static string ColumnOr(InoCard card, int index, string fallback) =>
        card.Columns is { } cols && cols.Length > index && !string.IsNullOrWhiteSpace(cols[index]) ? cols[index] : fallback;

    internal static IEnumerable<Node> RenderCardNodes(InoCard card)
    {
        switch (card.Kind)
        {
            case "slot":
                if (InoCards.ToLiveSlot(card.SourceNeuronUri) is { } directSlot) { yield return directSlot; }
                break;

            case "applet" or "unit":
                yield return Card(card.Title, card.Summary)[
                    HStack(Space.Sm)[
                        StatusPill(string.Equals(card.Status, "failed", StringComparison.OrdinalIgnoreCase) ? StatusKind.Error : StatusKind.Done, card.Status),
                        MutedText(StripNeuronUri(card.SourceNeuronUri)).Mono()
                    ],
                    InoCards.ToLiveSlot(card.SourceNeuronUri, string.Equals(card.Kind, "unit", StringComparison.OrdinalIgnoreCase) ? "unit" : "view")
                ];
                break;

            case "approval":
                // A tool result's card reports an approval; it never answers one. The plan is answered on the inbox approval (RenderApproval).
                var cardApprovalVisual = ResolveApprovalVisual(StripNeuronUri(card.SourceNeuronUri));
                yield return new ApprovalCardNode(
                    $"card-{card.Id}", card.Id, card.Title, card.Summary,
                    [
                        new ApprovalRowNode("Status", "pending", card.Status),
                        new ApprovalRowNode("Source", "local", StripNeuronUri(card.SourceNeuronUri)),
                        new ApprovalRowNode("Scope", "ino", StripNeuronUri(card.EffectiveScopeUri)),
                    ],
                    SystemName: "Ino",
                    Status: card.Status,
                    SourceNeuronUri: StripNeuronUri(card.SourceNeuronUri),
                    Icon: cardApprovalVisual.IconKey,
                    CapabilityLabel: cardApprovalVisual.Label);
                break;

            case "choice":
                yield return new ChoiceCardNode(
                    card.Id, card.Title,
                    [new ChoiceOptionNode("Primary Workspace", "circles/enterprise"), new ChoiceOptionNode("Executive Board Circle", "circles/board")],
                    PickedIndex: card.Status == "approved" ? 0 : null);
                break;

            case "draft":
                yield return new DraftCardNode(
                    card.Id, "email", "user@intochat.os", "team@intochat.os", card.Summary,
                    Subject: card.Title, Status: card.Status == "approved" ? "sent" : card.Status);
                break;

            case "secret":
                var savedSource = IsVaultSecretUri(card.SourceNeuronUri) ? card.SourceNeuronUri : StripNeuronUri(card.SourceNeuronUri);
                yield return new SecretCardNode(
                    card.Title, card.Summary,
                    SavedIn: string.IsNullOrWhiteSpace(savedSource) ? "Secure Vault" : $"Secure Vault ({savedSource})",
                    Status: card.Status == "approved" ? "saved" : card.Status,
                    Enter: PrimaryButton("Save to vault", Icons.KeyRound).Small().Key("enter-secret").WithIntent(IntentKind.OpenWindow, "os://window/secure").ToButton());
                break;

            case "app-install":
                yield return new RecordCardNode(
                    "Smart App Manifest", card.Title,
                    [
                        new RecordFieldNode("Publisher", "IntoChat OS (Verified)"),
                        new RecordFieldNode("Capabilities", "Files (read), Postgres (read)"),
                        new RecordFieldNode("Status", card.Status),
                    ],
                    Subtitle: card.Summary, LinkLabel: "Review & Install",
                    Open: PrimaryButton("Review & Install").Small().Key("review-install").WithIntent(IntentKind.OpenWindow, "os://sheet/install").ToButton());
                break;

            case "table" when card.Columns is { Length: > 0 } cols && card.Rows is { Length: > 0 } rows:
                yield return new DataTable(card.Title, cols, rows.Select(static r => (IReadOnlyList<string>)r).ToArray(), StripNeuronUri(card.SourceNeuronUri), From: "Postgres");
                break;

            case "chart" when card.ChartPoints is { Length: > 0 } points:
                yield return new Chart(card.ChartKind ?? "bar", card.Title, points.Select(static pt => new ChartPointNode(pt.Label, pt.Value)).ToArray(), SeriesName: "Current", SourceNeuronUri: StripNeuronUri(card.SourceNeuronUri), From: "Postgres", Prefix: "€");
                break;

            case "markdraw":
                var activeDoc = ColumnOr(card, 0, "/work/architecture.markdraw");
                var format = ColumnOr(card, 1, "markdraw");
                var rev = int.TryParse(ColumnOr(card, 2, "1"), out var parsedRev) ? parsedRev : 1;
                var content = ColumnOr(card, 3, MarkdrawApp.DefaultArchitectureScene);
                var exportPath = card.Columns is { Length: >= 5 } && !string.IsNullOrWhiteSpace(card.Columns[4]) ? card.Columns[4] : null;
                var elements = MarkdrawSceneEngine.ParseSceneElements(content);
                yield return new RecordCardNode(
                    "Markdraw · Diagram & Canvas Studio", card.Title,
                    [new RecordFieldNode("Status", card.Status), new RecordFieldNode("Workspace File", activeDoc), new RecordFieldNode("Elements", $"{elements.Length} item(s) · rev {rev}")],
                    Subtitle: card.Summary, LinkLabel: "Open Markdraw Studio",
                    Open: Button("Open Markdraw Studio").Small().Key("open-markdraw").EmitsOrIntent(AppSignals.OpenApp("markdraw", activeDoc)));
                yield return MarkdrawSceneEngine.BuildMarkdrawCanvasCard(
                    $"{card.Id}-canvas-rev-{rev}", card.Title, activeDoc, format, content, rev, card.Summary, elements,
                    [activeDoc, "/work/architecture.markdraw", "/work/pipeline-flow.markdraw"], exportPath, 460);
                break;

            default:
                if (IntoChat.OS.Modules.ArtCraft.ArtCraftCatalog.TryGet(card.Kind, out var studio))
                {
                    yield return new RecordCardNode(
                        $"{studio.Title} · {studio.Subtitle}", card.Title,
                        [new RecordFieldNode("Status", card.Status), new RecordFieldNode("Studio", StripNeuronUri(card.SourceNeuronUri)), new RecordFieldNode("Scope", StripNeuronUri(card.EffectiveScopeUri))],
                        Subtitle: card.Summary, LinkLabel: $"Open {studio.Title} Studio",
                        Open: Button($"Open {studio.Title} Studio").Small().Key($"open-{studio.Id}").EmitsOrIntent(AppSignals.OpenApp(studio.Id)));
                    yield return WebBrowser(studio.Id is "photocraft" or "filmcraft" ? studio.DefaultWebUrl.TrimEnd('/') : studio.StaticWebPath, card.Title, $"{card.Id}-studio", 420, "wasm-ui");
                }
                else
                {
                    yield return new RecordCardNode(
                        card.Kind, card.Title,
                        [new RecordFieldNode("Status", card.Status), new RecordFieldNode("Source", StripNeuronUri(card.SourceNeuronUri)), new RecordFieldNode("Scope", StripNeuronUri(card.EffectiveScopeUri))],
                        Subtitle: card.Summary, LinkLabel: card.Kind == "file" ? "Open in Files" : "Open",
                        Open: card.Kind == "file" ? Button("Open in Files").Small().Key("open-files").EmitsOrIntent(AppSignals.OpenApp("files")) : null);
                }
                break;
        }
    }

    private static Button EmitsOrIntent(this ButtonBuilder builder, Intent intent)
        => builder.WithIntent(intent.Kind, intent.Arg).ToButton();

    internal static InoAppState BuildScenarioSessionState(string scenarioId, UserSettingsState settings)
        => InoModel.DecideScenario(scenarioId, settings);
}
