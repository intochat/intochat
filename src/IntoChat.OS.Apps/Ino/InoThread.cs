using System.Text.Json;
using DigitalBrain.Log;
using DigitalBrain.UI;
using IntoChat.OS.Apps.Controls;
using IntoChat.OS.Host.Logging;
using IntoChat.OS.Host.Primitives;
using IntoChat.OS.Modules.AI.Agents;
using IntoChat.OS.Modules.AI.Models;
using IntoChat.OS.Modules.Apps;
using IntoChat.OS.Modules.Flutter.Conversations;
using IntoChat.OS.Modules.Flutter.Workspace;
using static IntoChat.OS.UI.Kit;

namespace IntoChat.OS.Apps.Ino;

/// <summary>
/// Projects a <see cref="ConversationView"/> into the <see cref="ThreadMessage"/> list drawn by Ino's chat thread.
/// </summary>
public static class InoThread
{
    public static IReadOnlyList<ThreadMessage> Messages(
        ConversationView view,
        IReadOnlyDictionary<string, AppManifest?> apps,
        string? links)
    {
        ArgumentNullException.ThrowIfNull(view);
        ArgumentNullException.ThrowIfNull(apps);

        var superseded = SupersededIds(view);
        var runsById = new Dictionary<string, AgentRun>(StringComparer.Ordinal);
        var toolArgs = new Dictionary<(string RunId, string CallId), string>();

        foreach (var run in view.Runs)
        {
            runsById[run.RunId] = run;
            foreach (var call in run.ToolCalls)
            {
                toolArgs[(run.RunId, call.CallId)] = call.Arguments;
            }
        }

        if (view.Active is { } activeRun)
        {
            runsById[activeRun.RunId] = activeRun;
            foreach (var call in activeRun.ToolCalls)
            {
                toolArgs[(activeRun.RunId, call.CallId)] = call.Arguments;
            }
        }

        var returnedByRun = new Dictionary<string, List<Returned>>(StringComparer.Ordinal);
        var replyRunIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in view.Entries)
        {
            switch (entry)
            {
                case Called called:
                    if (EntryIds.RunOf(called.Id) is { } callRun && EntryIds.CallOf(called.Id) is { } callId)
                    {
                        toolArgs[(callRun, callId)] = called.ArgsJson;
                    }
                    break;

                case Returned returned:
                    if (EntryIds.RunOf(returned.Id) is { } retRun)
                    {
                        if (!returnedByRun.TryGetValue(retRun, out var list))
                        {
                            list = [];
                            returnedByRun[retRun] = list;
                        }
                        list.Add(returned);
                    }
                    break;

                case Said { By: Participant.Assistant } agentSaid:
                    if (EntryIds.RunOf(agentSaid.Id) is { } repRun && agentSaid.Id == EntryIds.Reply(repRun))
                    {
                        replyRunIds.Add(repRun);
                    }
                    break;
            }
        }

        var emittedNonCompletedRuns = new HashSet<string>(StringComparer.Ordinal);
        var messages = new List<ThreadMessage>(view.Entries.Length + 2);

        foreach (var entry in view.Entries)
        {
            if (entry is not Said said)
            {
                continue;
            }

            if (said.By is Participant.Human)
            {
                messages.Add(new ThreadMessage(said.Id.Value, "user", said.Text, "completed"));

                if (EntryIds.RunOf(said.Id) is { } promptRunId
                    && !replyRunIds.Contains(promptRunId)
                    && !string.Equals(view.Active?.RunId, promptRunId, StringComparison.Ordinal)
                    && !superseded.Contains(EntryIds.Reply(promptRunId))
                    && runsById.TryGetValue(promptRunId, out var run)
                    && IsTerminalNonCompleted(run))
                {
                    emittedNonCompletedRuns.Add(promptRunId);
                    messages.Add(BuildNonCompletedReply(run, returnedByRun, toolArgs, apps));
                }

                continue;
            }

            if (said.By is Participant.Assistant)
            {
                if (superseded.Contains(said.Id))
                {
                    continue;
                }

                messages.Add(BuildAgentReply(said, runsById, returnedByRun, toolArgs, apps, links));
            }
        }

        // Any failed/cancelled/interrupted run without its own prompt entry (e.g., a failed regeneration of the latest turn).
        foreach (var run in view.Runs)
        {
            if (run.Posted
                || !IsTerminalNonCompleted(run)
                || emittedNonCompletedRuns.Contains(run.RunId)
                || replyRunIds.Contains(run.RunId)
                || string.Equals(view.Active?.RunId, run.RunId, StringComparison.Ordinal)
                || superseded.Contains(EntryIds.Reply(run.RunId)))
            {
                continue;
            }

            var bubble = BuildNonCompletedReply(run, returnedByRun, toolArgs, apps);
            if (messages.Count > 0 && messages[^1] is { Role: "assistant", IsFailed: true })
            {
                messages[^1] = bubble;
            }
            else
            {
                messages.Add(bubble);
            }
        }

        if (view.Active is { } active && !replyRunIds.Contains(active.RunId))
        {
            messages.Add(BuildRunningReply(active, returnedByRun, toolArgs, apps));
        }

        if (view.Active is null)
        {
            MarkLatestCompletedReplyRegenerable(messages);
        }

        return messages;
    }

    private static void MarkLatestCompletedReplyRegenerable(List<ThreadMessage> messages)
    {
        for (var i = messages.Count - 1; i >= 0; i--)
        {
            var entryId = new EntryId(messages[i].Id);
            if (EntryIds.RunOf(entryId) is not { Length: > 0 } runId)
            {
                continue;
            }
            if (messages[i] is { Role: "assistant", Status: "completed" } && entryId == EntryIds.Reply(runId))
            {
                messages[i] = messages[i] with { CanRegenerate = true };
            }
            break;
        }
    }

    internal static HashSet<EntryId> SupersededIds(ConversationView view)
    {
        var superseded = new HashSet<EntryId>();
        foreach (var entry in view.Entries)
        {
            if (entry is Said { Replaces: { } target })
            {
                superseded.Add(target);
            }
        }
        if (view.Replacing is { } replacing)
        {
            superseded.Add(replacing);
        }
        return superseded;
    }

    internal static bool HasLinkPosts(ConversationView view)
        => view.Entries.OfType<Said>().Any(static s => InoLinkCard.TryReadLinkPost(s.Id.Value, out _));

    internal static HashSet<string> ReferencedAppNames(ConversationView view, string? links)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        var toolArgs = new Dictionary<(string RunId, string CallId), string>();
        foreach (var run in view.Runs)
        {
            foreach (var call in run.ToolCalls)
            {
                toolArgs[(run.RunId, call.CallId)] = call.Arguments;
            }
        }
        if (view.Active is { } active)
        {
            foreach (var call in active.ToolCalls)
            {
                toolArgs[(active.RunId, call.CallId)] = call.Arguments;
            }
        }

        foreach (var entry in view.Entries)
        {
            switch (entry)
            {
                case Called called:
                    if (EntryIds.RunOf(called.Id) is { } callRun && EntryIds.CallOf(called.Id) is { } callId)
                    {
                        toolArgs[(callRun, callId)] = called.ArgsJson;
                    }
                    break;

                case Returned returned:
                    string? args = null;
                    if (EntryIds.RunOf(returned.Id) is { } retRun && EntryIds.CallOf(returned.Id) is { } retCall)
                    {
                        toolArgs.TryGetValue((retRun, retCall), out args);
                    }
                    if (Opened(returned, args) is { } appCard)
                    {
                        names.Add(appCard.App);
                    }
                    break;

                case Said said when InoLinkCard.TryReadLinkPost(said.Id.Value, out var request):
                    if (InoLinkCard.FindLink(links, request) is { } linkCard)
                    {
                        names.Add(linkCard.App);
                    }
                    break;
            }
        }

        return names;
    }

    internal static HashSet<string> ProjectedCardIds(ConversationView view)
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in view.Entries)
        {
            if (entry is Returned { Ok: true, ResultJson.Length: > 0 } returned
                && InoCards.FromResult(returned.ResultJson) is { } card)
            {
                ids.Add(card.Id);
            }
        }
        return ids;
    }

    // Absorbs AgentChatSource.Named: an app without a titled manifest is not installed.
    internal static InoLinkCard Named(InoLinkCard card, AppManifest? manifest)
        => manifest is { Title.Length: > 0 }
            ? card with { Title = manifest.Title, Icon = manifest.Icon ?? "", Installed = true }
            : card with { Title = card.App, Installed = false };

    // Absorbs AgentChatSource.Published: prefers publisher "os", else first listed publisher.
    internal static InoLinkCard Published(InoLinkCard card, IReadOnlyList<(string Publisher, string Name)> listings)
    {
        var apps = listings.Where(listing => listing.Name == card.App).Select(listing => listing.Publisher).ToList();
        var publisher = apps.Contains("os") ? "os" : apps.FirstOrDefault();
        return publisher is null ? card : card with { Publisher = publisher };
    }

    // Absorbs AgentChatSource.Opened for both S2 typed tools and legacy neurons_call.
    internal static InoLinkCard? Opened(Returned returned, string? argsJson = null)
    {
        if (!returned.Ok)
        {
            return null;
        }

        if (string.Equals(returned.Tool, "apps_app2_request", StringComparison.Ordinal))
        {
            return OpenedFromAppRequest(argsJson ?? returned.ResultJson);
        }

        if (string.Equals(returned.Tool, "ui_workspace_show", StringComparison.Ordinal))
        {
            return OpenedFromWorkspaceShow(argsJson ?? returned.ResultJson);
        }

        if (string.Equals(returned.Tool, "studio_launch", StringComparison.Ordinal))
        {
            return OpenedFromStudioLaunch(argsJson, returned.ResultJson);
        }

        if (string.Equals(returned.Tool, "neurons_call", StringComparison.Ordinal) && !string.IsNullOrWhiteSpace(argsJson))
        {
            return OpenedFromLegacyNeuronsCall(argsJson);
        }

        return null;
    }

    private static InoLinkCard? OpenedFromAppRequest(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) { return null; }
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) { return null; }
            var root = doc.RootElement;
            var op = GetString(root, "operation");
            if (!string.Equals(op, "open", StringComparison.Ordinal)) { return null; }
            var rawKey = GetString(root, "key") ?? GetString(root, "app");
            var name = ExtractAppName(rawKey);
            if (!InoLinkCard.IsAppName(name)) { return null; }
            var input = GetString(root, "input") ?? string.Empty;
            return new InoLinkCard(name!, name!, "", true, input);
        }
        catch (JsonException) { return null; }
    }

    private static InoLinkCard? OpenedFromWorkspaceShow(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) { return null; }
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) { return null; }
            var rawApp = GetString(doc.RootElement, "app");
            var name = ExtractAppName(rawApp);
            return InoLinkCard.IsAppName(name) ? new InoLinkCard(name!, name!, "", true) : null;
        }
        catch (JsonException) { return null; }
    }

    private static InoLinkCard? OpenedFromStudioLaunch(string? argsJson, string? resultJson)
    {
        if (!string.IsNullOrWhiteSpace(argsJson))
        {
            try
            {
                using var doc = JsonDocument.Parse(argsJson);
                if (doc.RootElement.ValueKind == JsonValueKind.Object)
                {
                    var name = ExtractAppName(GetString(doc.RootElement, "appId"));
                    if (InoLinkCard.IsAppName(name)) { return new InoLinkCard(name!, name!, "", true); }
                }
            }
            catch (JsonException exception) { OsConsoleLog.For(typeof(InoThread)).LogSwallowed(exception, "Unreadable studio_launch arguments"); }
        }

        if (!string.IsNullOrWhiteSpace(resultJson))
        {
            try
            {
                using var doc = JsonDocument.Parse(resultJson);
                if (doc.RootElement.ValueKind == JsonValueKind.Object)
                {
                    var text = GetString(doc.RootElement, "text");
                    if (text is not null && text.StartsWith("Opened ", StringComparison.Ordinal) && text.EndsWith('.'))
                    {
                        var candidate = text["Opened ".Length..^1].Trim();
                        if (InoLinkCard.IsAppName(candidate)) { return new InoLinkCard(candidate, candidate, "", true); }
                    }
                }
            }
            catch (JsonException exception) { OsConsoleLog.For(typeof(InoThread)).LogSwallowed(exception, "Unreadable studio_launch result"); }
        }

        return null;
    }

    private static InoLinkCard? OpenedFromLegacyNeuronsCall(string argsJson)
    {
        try
        {
            using var document = JsonDocument.Parse(argsJson);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) { return null; }
            var contract = GetString(root, "contract");
            var method = GetString(root, "method");
            using var arguments = JsonDocument.Parse(GetString(root, "arguments") ?? "[]");
            var list = arguments.RootElement;
            if (list.ValueKind != JsonValueKind.Array) { return null; }
            string? At(int index) => list.GetArrayLength() > index && list[index].ValueKind == JsonValueKind.String ? list[index].GetString() : null;
            if (contract == typeof(IApp).FullName && method == nameof(IApp.Request) && At(0) == "open")
            {
                var name = ExtractAppName(GetString(root, "key"));
                return InoLinkCard.IsAppName(name) ? new InoLinkCard(name!, name!, "", true, At(1) ?? "") : null;
            }
            if (contract == typeof(IWorkspace).FullName && method == nameof(IWorkspace.Show))
            {
                var name = ExtractAppName(At(0));
                return InoLinkCard.IsAppName(name) ? new InoLinkCard(name!, name!, "", true) : null;
            }
        }
        catch (JsonException exception) { OsConsoleLog.For(typeof(InoThread)).LogSwallowed(exception, "Unreadable legacy tool-call arguments"); }
        return null;
    }

    private static string? ExtractAppName(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) { return null; }
        var trimmed = raw.Trim();
        if (IntoChat.OS.Host.Primitives.AppRef.TryParse(trimmed, out var appRef)) { return appRef.Name; }
        var slash = trimmed.LastIndexOf('/');
        return slash >= 0 && slash + 1 < trimmed.Length ? trimmed[(slash + 1)..] : trimmed;
    }

    private static bool IsTerminalNonCompleted(AgentRun run)
        => run.Status is AgentRunStatus.Failed or AgentRunStatus.Interrupted or AgentRunStatus.Cancelled;

    private static ThreadMessage BuildAgentReply(
        Said said,
        Dictionary<string, AgentRun> runsById,
        Dictionary<string, List<Returned>> returnedByRun,
        Dictionary<(string RunId, string CallId), string> toolArgs,
        IReadOnlyDictionary<string, AppManifest?> apps,
        string? links)
    {
        var runId = EntryIds.RunOf(said.Id);
        var (displayText, inlineSlots) = InoCards.ExtractInlineSlots(said.Text);

        string? rawModel = said.Model?.Value;
        if (string.IsNullOrWhiteSpace(rawModel) && runId is not null && runsById.TryGetValue(runId, out var run))
        {
            rawModel = run.Posted ? null : run.Model?.Model;
        }
        var modelCard = InoView.ResolveModel(rawModel);

        var (activities, cards) = runId is not null && returnedByRun.TryGetValue(runId, out var returns)
            ? BuildStepVisuals(runId, returns, toolArgs, apps)
            : (Array.Empty<ActivityNode>(), new List<Node>());

        if (InoLinkCard.TryReadLinkPost(said.Id.Value, out var linkRequest)
            && InoLinkCard.FindLink(links, linkRequest) is { } linkCard)
        {
            apps.TryGetValue(linkCard.App, out var manifest);
            cards.Add(RenderAppCard(Named(linkCard, manifest)));
        }

        if (inlineSlots.Count > 0)
        {
            cards.AddRange(inlineSlots);
        }

        return new ThreadMessage(said.Id.Value, "assistant", displayText, "completed")
        {
            Model = modelCard?.Name ?? (string.IsNullOrWhiteSpace(rawModel) ? null : rawModel),
            ProviderIcon = modelCard?.IconKey(),
            Activities = activities,
            Cards = cards,
        };
    }

    private static ThreadMessage BuildNonCompletedReply(
        AgentRun run,
        Dictionary<string, List<Returned>> returnedByRun,
        Dictionary<(string RunId, string CallId), string> toolArgs,
        IReadOnlyDictionary<string, AppManifest?> apps)
    {
        var (displayText, inlineSlots) = InoCards.ExtractInlineSlots(run.Reply);
        var status = run.Status switch
        {
            AgentRunStatus.Cancelled => "cancelled",
            AgentRunStatus.Interrupted => "interrupted",
            _ => "failed",
        };
        var failureCode = run.Status switch
        {
            AgentRunStatus.Cancelled => "cancelled",
            AgentRunStatus.Interrupted => "interrupted",
            _ => AgentFailures.CodeOf(run.Failure),
        };
        var failureText = InoErrors.Describe(failureCode) ?? ChatThreadNeuron.NoAnswer;

        var rawModel = run.Model is { Provider: not AiProvider.Host, Model.Length: > 0 } resolved ? resolved.Model : null;
        var modelCard = InoView.ResolveModel(rawModel);

        var (activities, cards) = returnedByRun.TryGetValue(run.RunId, out var returns)
            ? BuildStepVisuals(run.RunId, returns, toolArgs, apps)
            : (Array.Empty<ActivityNode>(), new List<Node>());

        if (inlineSlots.Count > 0)
        {
            cards.AddRange(inlineSlots);
        }

        return new ThreadMessage(EntryIds.Reply(run.RunId).Value, "assistant", displayText, status)
        {
            Model = modelCard?.Name ?? rawModel,
            ProviderIcon = modelCard?.IconKey(),
            Failure = failureText,
            Activities = activities,
            Cards = cards,
        };
    }

    private static ThreadMessage BuildRunningReply(
        AgentRun active,
        Dictionary<string, List<Returned>> returnedByRun,
        Dictionary<(string RunId, string CallId), string> toolArgs,
        IReadOnlyDictionary<string, AppManifest?> apps)
    {
        var (displayText, inlineSlots) = InoCards.ExtractInlineSlots(active.Reply);
        var rawModel = active.Model is { Provider: not AiProvider.Host, Model.Length: > 0 } resolved ? resolved.Model : null;
        var modelCard = InoView.ResolveModel(rawModel);

        var (activities, cards) = returnedByRun.TryGetValue(active.RunId, out var returns)
            ? BuildStepVisuals(active.RunId, returns, toolArgs, apps)
            : (Array.Empty<ActivityNode>(), new List<Node>());

        if (inlineSlots.Count > 0)
        {
            cards.AddRange(inlineSlots);
        }

        return new ThreadMessage(EntryIds.Reply(active.RunId).Value, "assistant", displayText, "running")
        {
            Model = modelCard?.Name ?? rawModel,
            ProviderIcon = modelCard?.IconKey(),
            Activities = activities,
            Cards = cards,
        };
    }

    private static (ActivityNode[] Activities, List<Node> Cards) BuildStepVisuals(
        string runId,
        List<Returned> returns,
        Dictionary<(string RunId, string CallId), string> toolArgs,
        IReadOnlyDictionary<string, AppManifest?> apps)
    {
        var activities = new ActivityNode[returns.Count];
        var cards = new List<Node>();
        InoLinkCard? lastOpenedApp = null;

        for (var i = 0; i < returns.Count; i++)
        {
            var r = returns[i];
            var callId = EntryIds.CallOf(r.Id) ?? r.Id.Value;
            toolArgs.TryGetValue((runId, callId), out var argsJson);

            var formattedTitle = InoModel.FormatActivityTitle(r.Tool, argsJson);
            var errorText = r.Ok ? null : ExtractErrorText(r.ResultJson);
            var activity = new InoActivity(callId, formattedTitle, r.Ok ? r.ResultJson : null, errorText);
            activities[i] = InoView.ToActivityNode(activity);

            if (r.Ok)
            {
                if (r.Card is { Kind: "node", NodeJson.Length: > 0 } nodeCard)
                {
                    if (InoNodeCard.TryRead(nodeCard.NodeJson, out var parsedNode, out _))
                    {
                        cards.Add(Card(nodeCard.Title)[parsedNode]);
                    }
                }
                else if (!string.IsNullOrWhiteSpace(r.ResultJson) && InoCards.FromResult(r.ResultJson) is { } legacyCard)
                {
                    cards.AddRange(InoView.RenderCardNodes(legacyCard));
                }

                if (Opened(r, argsJson) is { } opened)
                {
                    lastOpenedApp = opened;
                }
            }
        }

        if (lastOpenedApp is not null)
        {
            apps.TryGetValue(lastOpenedApp.App, out var manifest);
            cards.Add(RenderAppCard(Named(lastOpenedApp, manifest)));
        }

        return (activities, cards);
    }

    private static Node RenderAppCard(InoLinkCard card) => new RecordCardNode(
        "App",
        card.Title,
        [
            new RecordFieldNode("App", card.App),
            new RecordFieldNode("Status", card.Installed ? "Installed" : "Not installed"),
        ],
        Subtitle: string.IsNullOrWhiteSpace(card.Input) ? null : card.Input,
        LinkLabel: card.Installed ? $"Open {card.Title}" : "Install",
        Open: card.Installed
            ? Button($"Open {card.Title}")
                .Small()
                .Key($"open-app:{card.App}")
                .WithIntent(IntentKind.OpenWindow, AppSignals.OpenApp(card.App, string.IsNullOrWhiteSpace(card.Input) ? null : card.Input).Arg)
                .ToButton()
            : null);

    private static string ExtractErrorText(string? resultJson)
    {
        if (string.IsNullOrWhiteSpace(resultJson)) { return "Tool failed."; }
        try
        {
            using var doc = JsonDocument.Parse(resultJson);
            if (doc.RootElement.ValueKind == JsonValueKind.Object)
            {
                if (GetString(doc.RootElement, "error") is { Length: > 0 } err) { return err; }
                if (GetString(doc.RootElement, "text") is { Length: > 0 } text) { return text; }
                if (GetString(doc.RootElement, "message") is { Length: > 0 } msg) { return msg; }
            }
        }
        catch (JsonException exception) { OsConsoleLog.For(typeof(InoThread)).LogSwallowed(exception, "Unreadable tool error payload"); }
        return resultJson;
    }

    private static string? GetString(JsonElement obj, string name)
        => obj.TryGetProperty(name, out var prop) && prop.ValueKind == JsonValueKind.String ? prop.GetString() : null;
}
