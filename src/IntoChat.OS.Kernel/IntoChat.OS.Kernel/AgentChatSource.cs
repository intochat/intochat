using DigitalBrain.Brains;
using DigitalBrain.Capabilities.Connections.Integrations;
using DigitalBrain.Log;
using System.Text.Json;
using DigitalBrain.Runtime.Orleans.Hosting;
using IntoChat.OS.Modules.AI.Agents;
using IntoChat.OS.Modules.Apps;
using IntoChat.OS.Modules.Flutter.Chat;
using IntoChat.OS.Modules.Flutter.Workspace;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace IntoChat.OS.Kernel;

// The OS composes AI and UI. Neither module gains an implementation dependency on the other.
internal sealed class AgentChatSource(IServiceProvider services) : IChatAgentSource
{
    // The agent is a plain neuron (06-06d): the chat holds a synapse on its signals.
    public ChatAgentSubscription Subscription(string key)
    {
        var brain = DigitalBrain.Brains.BrainKey.BrainOf(key);
        return new(new(new DigitalBrain.BrainId(brain), IntoChat.OS.Modules.AI.Agents.Signals.AgentSignals.NeuronType, key[(brain.Length + 1)..]),
            IntoChat.OS.Modules.AI.Agents.Signals.AgentSignals.All);
    }


    public async Task<ChatAgentSnapshot> Read(string key, long selectionRevision)
    {
        var state = await SiloNeurons.Get<IAgent>(services, key).Read();
        // A pasted link's app and input lived in a text neuron beside the conversations; that widget neuron is gone, so a link
        // post shows without its card.
        string? links = null;
        var runs = state.Runs.ToList();
        if (state.ActiveRun is { } active) { runs.RemoveAll(run => run.RunId == active.RunId); runs.Add(active); }
        var projection = new ChatProjection(key, selectionRevision, state.Version, await Named(key, [.. runs.SelectMany(run => Entries(run, links))]))
        {
            Actions = state.ActiveRun is { } running
                ? [new("stop/" + running.RunId, "Stop", JsonSerializer.Serialize(new { runId = running.RunId }))] : [],
        };
        var latest = state.ActiveRun ?? state.Runs.LastOrDefault();
        return new(projection, latest is null ? null : new(key, selectionRevision, state.Version,
            latest.RunId, latest.Status switch
            {
                AgentRunStatus.Running => ChatRunStatus.Running,
                AgentRunStatus.Completed => ChatRunStatus.Completed,
                AgentRunStatus.Failed => ChatRunStatus.Failed,
                AgentRunStatus.Cancelled => ChatRunStatus.Cancelled,
                AgentRunStatus.Interrupted => ChatRunStatus.Interrupted,
                _ => throw new InvalidOperationException("Unknown agent run status."),
            }));
    }

    // Each card is named from its app's manifest at read time: an app without a title is not installed.
    private async Task<ChatEntry[]> Named(string key, ChatEntry[] entries)
    {
        var scope = key.IndexOf("/apps/", StringComparison.Ordinal) is var cut and > 0 ? key[..cut] : DigitalBrain.Brains.BrainKey.BrainOf(key);
        var manifests = new Dictionary<string, AppManifest?>(StringComparer.Ordinal);
        IReadOnlyList<(string Publisher, string Name)>? listed = null;
        for (var i = 0; i < entries.Length; i++)
        {
            if (entries[i].App is not { } card) { continue; }
            if (!manifests.TryGetValue(card.App, out var manifest))
            {
                try { manifest = (await SiloNeurons.Get<IApp>(services, IApp.Key(scope, card.App)).Read()).Manifest; }
                catch (Exception error) when (error is not OperationCanceledException) { NullLogger.Instance.LogSwallowed(error, "App manifest unreadable"); manifest = null; }
                manifests[card.App] = manifest;
            }
            var named = Named(card, manifest);
            // An app that is not installed names its publisher from the store so the card can offer Install.
            if (!named.Installed && named.Publisher is null)
            {
                // The platform-wide listing, not the brain's gallery: this read runs in the chat's own deferred
                // reconciliation, where no person's brain is stamped, and the gallery answers nothing then.
                try
                {
                    listed ??= services.GetService<MarketplaceView>() is { } marketplace
                        ? [.. (await marketplace.List(default)).Select(listing => (listing.Publisher, listing.Name))] : [];
                }
                catch (Exception error) when (error is not OperationCanceledException) { NullLogger.Instance.LogSwallowed(error, "App listing unreadable"); listed = []; }
                named = Published(named, listed);
            }
            entries[i] = entries[i] with { App = named };
        }
        return entries;
    }

    internal static ChatAppCard Named(ChatAppCard card, AppManifest? manifest)
        => manifest is { Title.Length: > 0 }
            ? card with { Title = manifest.Title, Icon = manifest.Icon ?? "", Installed = true }
            : card with { Title = card.App, Installed = false };

    // The listing of an app (the os publisher first, else the first listed) gives an uninstalled card its publisher.
    internal static ChatAppCard Published(ChatAppCard card, IReadOnlyList<(string Publisher, string Name)> listings)
    {
        var apps = listings.Where(listing => listing.Name == card.App).Select(listing => listing.Publisher).ToList();
        var publisher = apps.Contains("os") ? "os" : apps.FirstOrDefault();
        return publisher is null ? card : card with { Publisher = publisher };
    }

    // The app a completed IApp.Request("open") or IWorkspace.Show tool call opened, else null.
    internal static ChatAppCard? Opened(AgentToolCall call)
    {
        if (call.Name != "neurons_call" || call.Result is null || call.Error is not null || call.Code is not null) { return null; }
        try
        {
            using var document = JsonDocument.Parse(call.Arguments);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) { return null; }
            string? Text(string name) => root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
            var contract = Text("contract");
            var method = Text("method");
            using var arguments = JsonDocument.Parse(Text("arguments") ?? "[]");
            var list = arguments.RootElement;
            if (list.ValueKind != JsonValueKind.Array) { return null; }
            string? At(int index) => list.GetArrayLength() > index && list[index].ValueKind == JsonValueKind.String ? list[index].GetString() : null;
            if (contract == typeof(IApp).FullName && method == nameof(IApp.Request) && At(0) == "open")
            {
                var name = Text("key") is { } app && app.LastIndexOf("/apps/", StringComparison.Ordinal) is var at and >= 0 ? app[(at + 6)..] : null;
                return ChatAppCard.IsAppName(name) ? new(name!, name!, "", true, At(1) ?? "") : null;
            }
            if (contract == typeof(IWorkspace).FullName && method == nameof(IWorkspace.Show))
            {
                var name = At(0) is { } app && app.LastIndexOf('/') is var at ? app[(at + 1)..] : null;
                return ChatAppCard.IsAppName(name) ? new(name!, name!, "", true) : null;
            }
        }
        catch (JsonException exception) { NullLogger.Instance.LogSwallowed(exception, "Unreadable tool-call arguments"); }
        return null;
    }

    // A posted run (IAgent.Post, e.g. a greeting) has no user turn; any other run keeps its user
    // bubble, even an attachment-only turn with empty text.
    internal static IEnumerable<ChatEntry> Entries(AgentRun run, string? links = null)
    {
        if (!run.Posted) { yield return new ChatEntry(run.RunId + "/user", ChatRole.User, run.UserText); }
        var card = run.Posted
            ? ChatAppCard.TryReadLinkPost(run.RunId, out var request) ? ChatAppCard.FindLink(links, request) : null
            : run.ToolCalls.Select(Opened).LastOrDefault(opened => opened is not null);
        yield return new ChatEntry(run.RunId + "/assistant", ChatRole.Assistant, run.Reply ?? "")
        {
            Model = run.Posted ? null : run.Model?.Model,
            App = card,
            Status = run.Status.ToString().ToLowerInvariant(),
            Error = run.Error,
            Activities = [.. run.ToolCalls.Select(call => new ChatActivity(call.CallId, call.Name, call.Arguments, call.Result, call.Error))],
        };
    }
}
