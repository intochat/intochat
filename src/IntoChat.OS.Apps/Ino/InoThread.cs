using System.Text.Json;
using DigitalBrain.Log;
using DigitalBrain.UI;
using IntoChat.OS.Apps.AgentApp;
using IntoChat.OS.Host.Logging;
using IntoChat.OS.Host.Primitives;
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

        var options = new ConversationThreadOptions
        {
            TransformAssistantText = static raw =>
            {
                var (cleanText, slots) = InoCards.ExtractInlineSlots(raw);
                return (cleanText, slots);
            },
            DescribeFailure = InoErrors.Describe,
            FormatActivity = static (returned, argsJson, callId) =>
            {
                var formattedTitle = InoModel.FormatActivityTitle(returned.Tool, argsJson);
                var errorText = returned.Ok ? null : ConversationThreadProjection.ExtractErrorText(returned.ResultJson);
                var activity = new InoActivity(callId, formattedTitle, returned.Ok ? returned.ResultJson : null, errorText);
                return InoView.ToActivityNode(activity);
            },
            AppendStepCards = static (returned, _, cards) =>
            {
                if (!string.IsNullOrWhiteSpace(returned.ResultJson) && InoCards.FromResult(returned.ResultJson) is { } legacyCard)
                {
                    cards.AddRange(InoView.RenderCardNodes(legacyCard));
                }
            },
            AppendRunCards = (runId, returns, toolArgs, cards) =>
            {
                InoLinkCard? lastOpenedApp = null;
                foreach (var r in returns)
                {
                    if (!r.Ok)
                    {
                        continue;
                    }

                    var callId = EntryIds.CallOf(r.Id) ?? r.Id.Value;
                    toolArgs.TryGetValue((runId, callId), out var argsJson);
                    if (Opened(r, argsJson) is { } opened)
                    {
                        lastOpenedApp = opened;
                    }
                }

                if (lastOpenedApp is not null)
                {
                    apps.TryGetValue(lastOpenedApp.App, out var manifest);
                    cards.Add(RenderAppCard(Named(lastOpenedApp, manifest)));
                }
            },
            AppendSaidCards = (said, cards) =>
            {
                if (InoLinkCard.TryReadLinkPost(said.Id.Value, out var linkRequest)
                    && InoLinkCard.FindLink(links, linkRequest) is { } linkCard)
                {
                    apps.TryGetValue(linkCard.App, out var manifest);
                    cards.Add(RenderAppCard(Named(linkCard, manifest)));
                }
            },
        };

        return ConversationThreadProjection.ToThreadMessages(view, options);
    }

    internal static HashSet<EntryId> SupersededIds(ConversationView view)
        => ConversationThreadProjection.SupersededIds(view);

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

    private static string? GetString(JsonElement obj, string name)
        => obj.TryGetProperty(name, out var prop) && prop.ValueKind == JsonValueKind.String ? prop.GetString() : null;
}
