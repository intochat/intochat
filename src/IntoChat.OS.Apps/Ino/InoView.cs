using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using DigitalBrain.Rules;
using DigitalBrain.UI;
using DigitalBrain.UI.Controls;
using IntoChat.OS.Apps.Controls;
using IntoChat.OS.Apps.Markdraw;
using IntoChat.OS.Host.Visuals;
using IntoChat.OS.Modules.AI.Models;
using IntoChat.OS.Modules.Apps;
using IntoChat.OS.Modules.Apps.Approvals;
using IntoChat.OS.Modules.Devices;
using IntoChat.OS.Modules.Flutter.Conversations;
using IntoChat.OS.UI;
using static IntoChat.OS.UI.Kit;
using UIEntry = DigitalBrain.UI.Entry;

namespace IntoChat.OS.Apps.Ino;

public static partial class InoView
{
    public const string ConversationKey = "conversation";
    public const string ComposerKey = "composer";
    public const string ComposerField = "apps/ino/composer";
    public const string ProviderCredentialKey = "provider-credential";
    public const string ProviderCredentialField = "apps/ino/provider-credential";
    public const string UseLocalKey = "use-local-model";
    public const string StopKey = ChatThreadNeuron.StopKey;
    public const string NewKey = "new-conversation";
    public const string SendDraftKey = "ino:send-draft";
    public const string DismissDraftKey = "ino:dismiss-draft";
    public const int MaxRenderedMessages = 100;
    public const int MaxRenderedChars = 1_000_000;

    private static readonly IReadOnlyDictionary<string, AppManifest?> EmptyApps = new Dictionary<string, AppManifest?>(StringComparer.Ordinal);

    /// <summary>The messages the conversation thread holds for <paramref name="s"/> and <paramref name="view"/>, capped to the tail budget plus any pending prompt.</summary>
    public static IReadOnlyList<ThreadMessage> Thread(
        InoAppState s,
        ConversationView? view = null,
        IReadOnlyDictionary<string, AppManifest?>? apps = null,
        string? links = null)
    {
        ArgumentNullException.ThrowIfNull(s);
        var source = view is not null ? InoThread.Messages(view, apps ?? EmptyApps, links) : [];
        return ConversationThreadProjection.CapAndAppendPending(
            source,
            pendingId: s.Pending?.Id,
            pendingText: s.Pending?.Text,
            maxMessages: MaxRenderedMessages,
            maxChars: MaxRenderedChars);
    }

    /// <summary>What Ino decides about its conversation thread: the composer, the model, the status and an error no message carries.</summary>
    public static ChatThreadProps ThreadProps(
        InoAppState s,
        VoiceAbilities voice,
        string? conversation,
        string? threadFailure = null,
        ConversationView? view = null,
        IReadOnlyList<ModelCard>? cards = null,
        IReadOnlyList<ModelAvailability>? availability = null,
        DeviceView? device = null)
    {
        ArgumentNullException.ThrowIfNull(s);
        ArgumentNullException.ThrowIfNull(voice);
        var shown = Thread(s, view);
        var hasDraft = !string.IsNullOrWhiteSpace(s.DraftPrompt);
        var placeholder = hasDraft
            ? $"Ask Ino: {CompactSingleLine(SanitizePersonVisibleText(s.DraftPrompt!), 96)}"
            : "Ask anything.";
        var emptyText = hasDraft
            ? $"Ready to ask: {CompactSingleLine(SanitizePersonVisibleText(s.DraftPrompt!), 120)}"
            : "What do you need?";
        ChatThreadError? explicitError = null;
        if (s.Error is { } error)
        {
            var line = InoErrors.Of(error);
            explicitError = new ChatThreadError(line.What, line.Detail, line.ActionLabel, line.Open, line.Retry);
        }

        return ConversationThreadProjection.ToThreadProps(
            view,
            new ConversationThreadPropsOptions
            {
                Placeholder = placeholder,
                EmptyText = emptyText,
                ConversationKey = InoModel.ConversationKeyOf(s.ConversationId),
                SelectedModel = s.SelectedModel,
                DeriveModelFromView = false,
                Working = s.ActiveRunId is not null,
                Blocked = s.Blocked,
                HasPending = s.Pending is not null,
                StatusOverride = ConversationStatusOf(s),
                ThreadFailure = threadFailure,
                Error = explicitError,
                DeriveErrorFromView = false,
                ShownMessages = shown,
                Models = cards is { Count: > 0 } ? Choices(cards, availability ?? [], device) : null,
                Dictation = voice.Hear,
                ReadAloud = voice.Speak,
                TalkConversation = voice.Talk && !string.IsNullOrWhiteSpace(conversation) ? conversation : null,
            });
    }

    /// <summary>What Ino decides about its conversation thread: the composer, the model, the status and an error no message carries.</summary>
    public static ChatThreadProps ThreadProps(
        InoAppState s,
        ConversationView? view = null,
        VoiceAbilities? voice = null,
        string? threadFailure = null,
        IReadOnlyList<ModelCard>? cards = null,
        IReadOnlyList<ModelAvailability>? availability = null,
        DeviceView? device = null)
    {
        ArgumentNullException.ThrowIfNull(s);
        return ThreadProps(s, voice ?? VoiceAbilities.None, s.ConversationId, threadFailure, view, cards, availability, device);
    }

    /// <summary>Projects text-capable model cards and availability into composer picker choices split into local and cloud sections.</summary>
    public static IReadOnlyList<ModelChoice> Choices(
        IReadOnlyList<ModelCard> cards,
        IReadOnlyList<ModelAvailability> availability,
        DeviceView? device = null)
    {
        ArgumentNullException.ThrowIfNull(cards);
        ArgumentNullException.ThrowIfNull(availability);

        var byId = new Dictionary<string, ModelAvailability>(availability.Count, StringComparer.OrdinalIgnoreCase);
        foreach (var entry in availability)
        {
            byId[entry.Model.Value] = entry;
        }

        Dictionary<string, Install>? activeInstalls = null;
        Dictionary<string, DeviceModel>? deviceModels = null;
        Dictionary<string, RuntimeStatus>? runtimesById = null;
        if (device is not null)
        {
            activeInstalls = new Dictionary<string, Install>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in device.Queue)
            {
                if (entry.State is InstallState.Queued or InstallState.Downloading or InstallState.Verifying)
                {
                    activeInstalls.TryAdd(entry.Model.Value, entry);
                }
            }

            deviceModels = new Dictionary<string, DeviceModel>(device.Models.Length, StringComparer.OrdinalIgnoreCase);
            foreach (var model in device.Models)
            {
                deviceModels.TryAdd(model.Card.Id.Value, model);
            }

            runtimesById = new Dictionary<string, RuntimeStatus>(device.Runtimes.Length, StringComparer.OrdinalIgnoreCase);
            foreach (var runtime in device.Runtimes)
            {
                runtimesById.TryAdd(runtime.Id.Value, runtime);
            }
        }

        var result = new List<ModelChoice>(cards.Count);
        foreach (var card in cards)
        {
            if (!card.Can.HasFlag(Abilities.Text)
                || card.Provider == AiProvider.TypeSafe
                || card.Id == KnownModels.LocalJev.Id)
            {
                continue;
            }

            var hasAvail = byId.TryGetValue(card.Id.Value, out var avail);
            var ready = !hasAvail || avail!.Ready;
            var note = ready ? null : avail!.Reason;
            double? progress = null;

            if (activeInstalls is not null && activeInstalls.TryGetValue(card.Id.Value, out var install))
            {
                ready = false;
                var clamped = Math.Clamp(install.Percent, 0d, 100d);
                (note, progress) = install.State switch
                {
                    InstallState.Downloading => ($"Downloading {(int)Math.Floor(clamped)}%", clamped / 100.0),
                    InstallState.Verifying => ("Verifying", 1.0),
                    _ => ("Queued", 0.0),
                };
            }
            else if (card.Where == Where.Device && !ready && device is not null)
            {
                deviceModels!.TryGetValue(card.Id.Value, out var deviceModel);
                var runtimeKnownDown = deviceModel is not null
                    && runtimesById!.TryGetValue(deviceModel.Runtime.Value, out var runtimeStatus)
                    && !runtimeStatus.Reachable;

                if (deviceModel is { Fit: Fit.WontFit })
                {
                    note = "Too big for this device";
                }
                else if (!runtimeKnownDown
                    && (deviceModel is not null || string.Equals(note, "Not installed", StringComparison.OrdinalIgnoreCase)))
                {
                    note = FormatInstallNote(card.Bytes ?? deviceModel?.Card.Bytes, deviceModel?.Fit);
                }
            }

            var abilities = new List<string>(4);
            if (card.Can.HasFlag(Abilities.Tools)) { abilities.Add("tools"); }
            if (card.Can.HasFlag(Abilities.Vision)) { abilities.Add("vision"); }
            if ((card.Can & (Abilities.Hear | Abilities.Speak | Abilities.Talk)) != 0) { abilities.Add("voice"); }
            if (card.Can.HasFlag(Abilities.Reasoning)) { abilities.Add("reasoning"); }

            result.Add(new ModelChoice(
                Id: card.Id.Value,
                Label: card.Name,
                Section: card.Where == Where.Device ? "local" : "cloud",
                Icon: card.IconKey(),
                Abilities: abilities.Count > 0 ? abilities : null,
                Available: ready,
                Note: note,
                Progress: progress));
        }

        return result;
    }

    private static string FormatInstallNote(long? bytes, Fit? fit)
    {
        var parts = new List<string>(3) { "Install" };
        if (bytes is > 0)
        {
            const long mib = 1024L * 1024L;
            const long gib = 1024L * mib;
            parts.Add(bytes.Value >= gib
                ? string.Create(CultureInfo.InvariantCulture, $"{bytes.Value / (double)gib:0.#} GB")
                : string.Create(CultureInfo.InvariantCulture, $"{Math.Max(1L, bytes.Value / mib)} MB"));
        }

        if (fit is { } value)
        {
            parts.Add(value switch
            {
                Fit.Good => "Good",
                Fit.Slow => "Slow",
                _ => value.ToString(),
            });
        }

        return string.Join(" · ", parts);
    }
    /// <summary>The 20 newest sessions drawn when Ino's <c>chats</c> section is open, each switching conversation.</summary>
    public static Node Sessions(SessionPage page, string current)
    {
        ArgumentNullException.ThrowIfNull(page);
        var items = page.Entries.Take(20).ToArray();
        Node[] navs =
        [
            .. items.Select(session =>
            {
                var isCurrent = string.Equals(session.Id, current, StringComparison.Ordinal);
                var label = string.IsNullOrWhiteSpace(session.Title)
                    ? $"Chat · {session.Id[..Math.Min(8, session.Id.Length)]}"
                    : session.Title;
                return NavItem(
                    $"session-{session.Id}",
                    label,
                    Icons.Sparkles,
                    "switch-conversation",
                    selected: isCurrent,
                    badge: isCurrent ? "Current" : null,
                    emit: AppSignals.Emits("ino", new InoSwitchConversation(session.Id)));
            }),
        ];

        var collection = new Collection(
        [
            .. items.Select(session =>
            {
                var isCurrent = string.Equals(session.Id, current, StringComparison.Ordinal);
                var label = string.IsNullOrWhiteSpace(session.Title)
                    ? $"Chat · {session.Id[..Math.Min(8, session.Id.Length)]}"
                    : session.Title;
                return new UIEntry(session.Id, label, isCurrent ? "Current" : "Switch");
            }),
        ]);

        return Surface(SurfaceKind.Acrylic)[
            VStack(Space.Xs)[
                HStack(Space.Xs)[
                    Heading($"Chats ({items.Length})", 3),
                    PrimaryButton("+ New", Icons.Plus).Small().Key(NewKey).Emits(AppSignals.Emit("ino", "new")).ToNode()
                ],
                VStack(Space.Xs, navs),
                collection
            ]
        ];
    }

    /// <summary>
    /// Ino's screen: the conversation thread (<paramref name="thread"/> draws its slot with the props) and what waits on the user under it
    /// (approvals, model setup, cards no message carries).
    /// </summary>
    public static View Screen(
        InoAppState s,
        VoiceAbilities voice,
        Func<ChatThreadProps, Node> thread,
        ConversationView? view = null,
        SessionPage? sessions = null,
        string? threadFailure = null,
        Rejected? departureRejection = null,
        IReadOnlyList<ModelCard>? cards = null,
        IReadOnlyList<ModelAvailability>? availability = null,
        DeviceView? device = null)
        => new("Ino", ScreenNode(s, voice, thread, view, sessions, threadFailure, departureRejection, cards, availability, device));

    /// <summary>Projects Ino's root <see cref="Node"/> for <see cref="AggregateScreen{TState}"/> and detached previews.</summary>
    public static Node ScreenNode(
        InoAppState s,
        VoiceAbilities voice,
        Func<ChatThreadProps, Node> thread,
        ConversationView? view = null,
        SessionPage? sessions = null,
        string? threadFailure = null,
        Rejected? departureRejection = null,
        IReadOnlyList<ModelCard>? cards = null,
        IReadOnlyList<ModelAvailability>? availability = null,
        DeviceView? device = null)
    {
        ArgumentNullException.ThrowIfNull(s);
        ArgumentNullException.ThrowIfNull(voice);
        ArgumentNullException.ThrowIfNull(thread);

        var projectedCardIds = view is not null ? InoThread.ProjectedCardIds(view) : [];
        Node[] loose = [.. s.ActiveCards.Where(c => !projectedCardIds.Contains(c.Id)).SelectMany(RenderCardNodes)];
        Node[] setup = ShouldShowModelSetup(s) ? RenderModelSetupNodes(s, device) : [];
        Node[] approvals = s.Approvals is { Length: > 0 } pending ? [.. pending.Select(RenderApproval)] : [];
        var navAboveList = new List<Node>();
        if (string.Equals(s.ActiveSection, "chats", StringComparison.Ordinal) && sessions is not null)
        {
            navAboveList.Add(Sessions(sessions, s.ConversationId));
        }
        navAboveList.AddRange(RenderNavigationSeedNodes(s, departureRejection));
        Node[] navAbove = [.. navAboveList];

        var conversation = thread(ThreadProps(s, voice, s.ConversationId, threadFailure, view, cards, availability, device));
        if (HasUnprojectedVaultSecretPrompt(s, view))
        {
            Node[] above = [.. navAbove, .. s.ActiveCards.SelectMany(RenderCardNodes)];
            Node[] below = [.. approvals, .. setup];
            return Filling(above, conversation, below);
        }

        Node[] under = [.. approvals, .. loose, .. setup];
        return Filling(navAbove, conversation, under);
    }

    private static Node[] RenderNavigationSeedNodes(InoAppState s, Rejected? departureRejection)
    {
        var hasDraft = !string.IsNullOrWhiteSpace(s.DraftPrompt);
        var hasFocus = !string.IsNullOrWhiteSpace(s.ActiveFocusTarget);
        if (departureRejection is null && !hasDraft && !hasFocus)
        {
            return [];
        }

        var nodes = new List<Node>(2);
        if (departureRejection is not null)
        {
            var message = departureRejection.Messages is { Length: > 0 } msgs && !string.IsNullOrWhiteSpace(msgs[0])
                ? msgs[0]
                : "Finish or discard provider credential setup before leaving.";
            nodes.Add(Banner("Provider setup unsaved", message, tone: "warning"));
        }

        if (hasDraft || hasFocus)
        {
            var canSend = s.ActiveRunId is null && s.Pending is null && !s.Blocked;
            var focusLabel = FormatFocusTargetLabel(s.ActiveFocusTarget);
            List<Node?> headerItems =
            [
                StatusPill(StatusKind.NeedsYou, "Ask Ino"),
                focusLabel is { Length: > 0 } ? StatusPill(StatusKind.Idle, $"Context · {focusLabel}") : null,
                hasDraft
                    ? PrimaryButton("Send Prompt", Icons.Sparkles)
                        .Small()
                        .Key(SendDraftKey)
                        .Disabled(!canSend)
                        .Emits(AppSignals.Emits("ino", new InoSend(Message: s.DraftPrompt)))
                        .ToNode()
                    : null,
                GhostButton("Dismiss")
                    .Small()
                    .Key(DismissDraftKey)
                    .OnClick<InoAppState>(x => x.DismissNavigationSeed())
                    .ToNode(),
            ];

            nodes.Add(Surface(SurfaceKind.Acrylic)[
                VStack(Space.Xs)[
                    HStack(Space.Xs, [.. headerItems]),
                    hasDraft ? Text(SanitizePersonVisibleText(s.DraftPrompt!)).ToNode() : null
                ]
            ]);
        }

        return [.. nodes];
    }

    private static string CompactSingleLine(string raw, int maxChars)
    {
        var single = raw.ReplaceLineEndings(" ").Trim();
        return single.Length <= maxChars ? single : single[..(maxChars - 1)] + "…";
    }

    private static string SanitizePersonVisibleText(string raw)
    {
        var clean = StripNeuronUri(raw);
        clean = clean.Replace("app.ino", "Ino", StringComparison.OrdinalIgnoreCase)
            .Replace("ino.send", "send", StringComparison.OrdinalIgnoreCase)
            .Replace("InoApp", "Ino", StringComparison.Ordinal)
            .Replace("IntoChat.", string.Empty, StringComparison.Ordinal);
        return clean.Trim();
    }

    internal static string FormatFocusTargetLabel(string? rawFocus)
    {
        if (string.IsNullOrWhiteSpace(rawFocus))
        {
            return string.Empty;
        }

        var clean = StripNeuronUri(rawFocus);
        foreach (var prefix in new[] { "focus:", "node:", "window:" })
        {
            if (clean.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                clean = clean[prefix.Length..].Trim();
                break;
            }
        }

        if (clean.StartsWith("app.", StringComparison.OrdinalIgnoreCase) && clean.Length > 4)
        {
            var slug = clean[4..];
            clean = char.ToUpperInvariant(slug[0]) + slug[1..];
        }

        return SanitizePersonVisibleText(clean);
    }

    // The thread fills the height a bounded parent gives (extent 1); what sits above and below it stretches to the width (extent -1).
    private static Node Filling(Node[] above, Node thread, Node[] below)
        => VStack(Space.Md, [.. above, thread, .. below]).Extents([.. above.Select(static _ => -1d), 1d, .. below.Select(static _ => -1d)]);

    internal static bool IsVaultSecretUri(string? uri)
        => !string.IsNullOrWhiteSpace(uri) && uri.Contains("/vault/secrets", StringComparison.OrdinalIgnoreCase);

    internal static bool HasUnprojectedVaultSecretPrompt(InoAppState s, ConversationView? view = null)
        => (view is null || view.Entries.Length == 0)
            && s.Pending is null
            && s.ActiveCards.Any(static c => string.Equals(c.Kind, "secret", StringComparison.Ordinal) && IsVaultSecretUri(c.SourceNeuronUri));

    internal static ActivityNode ToActivityNode(InoActivity a)
    {
        var (visual, detail) = ResolveActivityPresentation(a);
        return new ActivityNode(a.Id, visual.Label, a.Error is { Length: > 0 } ? "failed" : "done", detail, visual.IconKey, visual.Category);
    }

    private static string ConversationStatusOf(InoAppState s)
        => s.Blocked || string.Equals(s.Status, InoModel.Paused, StringComparison.Ordinal) ? "paused"
            : s.ActiveRunId is not null || s.Pending is not null || string.Equals(s.Status, InoModel.Working, StringComparison.Ordinal) ? "working"
            : s.Error is not null || string.Equals(s.Status, InoModel.Failed, StringComparison.Ordinal) ? "failed"
            : "ready";

    public static ModelCard? ResolveModel(string? model)
        => KnownModels.Find(model);

    public static VisualDescriptor ResolveApprovalVisual(string? sourceOrSubject)
        => VisualCatalog.ForApproval(sourceOrSubject);

    public static VisualDescriptor ResolveActivityVisual(InoActivity activity)
        => ResolveActivityPresentation(activity).Visual;

    public static (VisualDescriptor Visual, string? Detail) ResolveActivityPresentation(InoActivity activity)
    {
        ArgumentNullException.ThrowIfNull(activity);
        var rawResult = StripNeuronUri(activity.Error ?? activity.Result);
        var resultDetail = string.IsNullOrWhiteSpace(rawResult) ? null : rawResult;

        if (string.IsNullOrWhiteSpace(activity.Title))
        {
            return (new VisualDescriptor("Tool call", "capability:studio", "#64748B", "Tool"), resultDetail);
        }

        var clean = StripNeuronUri(activity.Title);
        if (clean.IndexOf(" · ", StringComparison.Ordinal) is > 0 and var sep)
        {
            var head = clean[..sep].Trim();
            var tail = clean[(sep + 3)..].Trim();
            if (VisualCatalog.TryForContract(head, out var preformatted))
            {
                return (preformatted, CombineActivityDetail(string.IsNullOrWhiteSpace(tail) ? null : tail, resultDetail));
            }
        }

        if (VisualCatalog.TryForContract(clean, out var direct))
        {
            var action = ExtractMethodSuffix(clean, direct.Label);
            return (direct, CombineActivityDetail(action, resultDetail));
        }

        var fallback = clean switch
        {
            "neurons_search" => new VisualDescriptor("Capability Search", "capability:studio", "#6366F1", "Studio"),
            "neurons_list" => new VisualDescriptor("Capability List", "capability:studio", "#6366F1", "Studio"),
            "neurons_context" => new VisualDescriptor("Brain Context", "brand:intochat", "#6366F1", "Studio"),
            _ => new VisualDescriptor(StripInternalToolLabel(clean), "capability:studio", "#64748B", "Tool"),
        };
        return (fallback, resultDetail);
    }

    private static string? CombineActivityDetail(string? action, string? result)
        => (action, result) switch
        {
            ({ Length: > 0 } a, { Length: > 0 } r) => $"{a} · {r}",
            ({ Length: > 0 } a, _) => a,
            (_, { Length: > 0 } r) => r,
            _ => null,
        };

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

    internal static Node[] RenderModelSetupNodes(InoAppState s, DeviceView? device = null)
    {
        var liveLocal = InoModelSetup.InstalledLocalModels(device);
        var localModels = liveLocal.Length > 0
            ? liveLocal
            : s.DetectedLocalModels is { Length: > 0 } list
                ? list
                : s.DetectedLocalModel is { Length: > 0 } single
                    ? [single]
                    : [];
        var preferredLocal = liveLocal.Length > 0 ? liveLocal[0] : s.DetectedLocalModel;
        ChoiceOptionNode[] options =
        [
            .. localModels.Take(3).Select(static m => new ChoiceOptionNode($"Local Ollama · {m}", "Ready on this machine")),
            new("OpenAI", "Cloud API key in Secure store"),
            new("Anthropic", "Cloud API key in Secure store"),
            new("Google Gemini", "Cloud API key in Secure store"),
            new("OpenRouter", "Cloud API key in Secure store"),
        ];

        var activeDownload = device?.Queue.FirstOrDefault(static q => q.State is InstallState.Downloading or InstallState.Queued or InstallState.Verifying);
        var subtitle = activeDownload is not null
            ? $"Preparing local model {activeDownload.Model.Value} ({activeDownload.State.ToString().ToLowerInvariant()}){(activeDownload.Percent > 0 ? $" · {(int)Math.Round(activeDownload.Percent)}%" : "")}. You can also paste a cloud API key below."
            : preferredLocal is { Length: > 0 } local
                ? $"Local Ollama model {local} was detected on this machine, or paste a provider API key below."
                : "Start local Ollama or paste an OpenAI, Anthropic, Google, or OpenRouter API key below.";
        Node? useLocal = preferredLocal is { Length: > 0 } preferred
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

    internal static Node RenderApproval(Approval approval)
    {
        var visual = ResolveApprovalVisual(approval.Subject);
        return new ApprovalCardNode(
            $"approval-{approval.PlanId}", approval.PlanId, approval.Title, approval.Detail,
            [new ApprovalRowNode("Subject", "", approval.Subject)],
            SystemName: "Ino", Status: "pending",
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
                    [new ChoiceOptionNode("Primary Workspace", "circles/enterprise"), new ChoiceOptionNode("Executive Board Group", "circles/board")],
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

            case "node":
                if (!string.IsNullOrWhiteSpace(card.Node) && InoNodeCard.TryRead(card.Node, out var parsedNode, out _))
                {
                    yield return Card(card.Title, card.Summary)[parsedNode];
                }
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

            case "engineer":
                var engRepo = ColumnOr(card, 0, "os");
                var engTopology = ColumnOr(card, 1, "1x LocalGit");
                var engStageOrSha = ColumnOr(card, 2, card.Status);
                yield return new RecordCardNode(
                    "IntoChat Engineer · Multi-Worktree Pipeline", card.Title,
                    [
                        new RecordFieldNode("Status", card.Status),
                        new RecordFieldNode("Repository", engRepo),
                        new RecordFieldNode("Worktrees", engTopology),
                        new RecordFieldNode("Checkpoint", engStageOrSha),
                    ],
                    Subtitle: card.Summary,
                    LinkLabel: "Open Engineer Workbench",
                    Open: Button("Open Engineer Workbench").Small().Key("open-engineer").EmitsOrIntent(AppSignals.OpenApp("engineer", card.Id)));
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
}
