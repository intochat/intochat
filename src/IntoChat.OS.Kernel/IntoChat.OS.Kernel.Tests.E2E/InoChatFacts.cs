using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using IntoChat.OS.AI.Agents;
using IntoChat.OS.AI.Models;
using IntoChat.OS.Apps;
using IntoChat.OS.Flutter;
using IntoChat.OS.Flutter.Button;
using IntoChat.OS.Flutter.Chat;
using IntoChat.OS.Flutter.TextField;
using IntoChat.OS.Flutter.Views;
using IntoChat.OS.Testing.Model;

namespace IntoChat.OS.Kernel.Tests.E2E;

// R4's Ino chat window on the real script runtime: the sidebar (new, rename, archive with Undo, Settings'
// unarchive), the per-chat model menu, a pasted workspace link and the upgrade from 3.1.3. The fake model
// answers, so a brain with the fixture key answers normally and never shows the first-run card.
[Collection("Router host")]
public sealed class InoChatFacts(RouterHostFixture host) : BrainFact(host)
{
    private const string InoPrompt = "You are the user's assistant";
    private const string IntendPrompt = "Your only job is to rephrase it";
    private const string Answer = "Here is a plan.";
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(90);

    private string InoKey => IApp.Key(WorkspaceId, "ino");

    [Fact(Timeout = 360_000)]
    public async Task TheInoAppIsVersion330()
    {
        var ct = TestContext.Current.CancellationToken;
        using var caller = User.AsCaller();
        Assert.Equal("3.3.0", ShippedAppsFixture.Features.Single(feature => feature.Owner == "os" && feature.Bundle.Name == "ino").Bundle.Version);
        var installed = await TestWait.UntilAsync(_ => Brain.Get<IApp>(InoKey).Read(), snapshot => snapshot.Status == AppStatus.Live, Patience, ct);
        Assert.Equal("3.3.0", installed.Version);
    }

    // Carried from Task 5: a fixture-key brain answers (no first-run card, no failed turn) and the new chat is
    // listed under its first message.
    [Fact(Timeout = 360_000)]
    public async Task NewChatAppearsInTheSidebarTitledByItsFirstMessage()
    {
        var ct = TestContext.Current.CancellationToken;
        using var caller = User.AsCaller();
        using var model = Answering();
        await AppRuntimeReadiness.WaitAsync(Brain, User, ct);
        var data = await DataKey(ct);

        var id = await NewChat(data, "Plan my week of meals", ct);
        var nodes = await Until(view => view.Any(node => node.Kind == UiNodeKind.Button && node.Action == "chat:" + id && node.Text == "Plan my week of meals"), ct);
        Assert.DoesNotContain(nodes, node => node.Key == "first-run");
        var run = await Answered(data, id, ct);
        Assert.Equal(AgentRunStatus.Completed, run.Status);
        Assert.Contains(Answer, run.Reply, StringComparison.Ordinal);
        var transcript = await TestWait.UntilAsync(_ => Brain.Get<IChat>(data + "/transcript").Read(),
            state => state.Projection?.Messages.Any(message => message.Role == ChatRole.Assistant && message.Text.Contains(Answer, StringComparison.Ordinal)) == true, Patience, ct);
        Assert.DoesNotContain(transcript.Projection!.Messages, message => message.Status == "failed");
    }

    [Fact(Timeout = 360_000)]
    public async Task RenameSurvivesARestart()
    {
        var ct = TestContext.Current.CancellationToken;
        using var caller = User.AsCaller();
        using var model = Answering();
        await AppRuntimeReadiness.WaitAsync(Brain, User, ct);
        var data = await DataKey(ct);
        var id = await NewChat(data, "Ideas for the garden", ct);
        await Until(view => view.Any(node => node.Action == "rename:" + id), ct);

        await Click("rename:" + id, ct);
        await Until(view => view.Any(node => node.Key == "rename-save"), ct);
        await Brain.Get<ITextField>(data + "/rename").SetValue("Garden plans");
        await Click("rename-save", ct);
        await Until(view => view.Any(node => node.Action == "chat:" + id && node.Text == "Garden plans") && view.All(node => node.Key != "rename-save"), ct);

        await Restart(ct);
        await Until(view => view.Any(node => node.Action == "chat:" + id && node.Text == "Garden plans"), ct);
        Assert.Equal("Garden plans", (await Index(data).Get(id))!.Title);
    }

    [Fact(Timeout = 360_000)]
    public async Task ArchiveUndoAndUnarchiveInSettings()
    {
        var ct = TestContext.Current.CancellationToken;
        using var caller = User.AsCaller();
        using var model = Answering();
        await AppRuntimeReadiness.WaitAsync(Brain, User, ct);
        var data = await DataKey(ct);
        var first = await NewChat(data, "Trip to Lisbon", ct);
        var second = await NewChat(data, "Birthday gift ideas", ct);
        await Until(view => view.Any(node => node.Action == "archive:" + first) && view.Any(node => node.Action == "archive:" + second), ct);

        // Archive shows the toast; its Undo puts the chat back.
        await Click("archive:" + first, ct);
        var toasted = await Until(view => view.Any(node => node.Kind == UiNodeKind.Toast && node.Text == "Chat archived.")
            && view.Any(node => node.Action == "undo-archive:" + first), ct);
        Assert.DoesNotContain(toasted, node => node.Action == "chat:" + first);
        await Click("undo-archive:" + first, ct);
        await Until(view => view.Any(node => node.Action == "chat:" + first), ct);
        Assert.False((await Index(data).Get(first))!.Archived);

        // Archived again, it leaves the sidebar and is listed by Settings, whose Unarchive brings it back.
        await Click("archive:" + first, ct);
        await Until(view => view.All(node => node.Action != "chat:" + first) && view.Any(node => node.Action == "chat:" + second), ct);
        using (var listed = await User.Http.GetAsync($"/brains/{WorkspaceId}/chats?archived=true", ct))
        {
            Assert.Equal(HttpStatusCode.OK, listed.StatusCode);
            var chats = (await listed.Content.ReadFromJsonAsync<JsonElement>(ct)).GetProperty("chats").EnumerateArray().ToArray();
            var archived = Assert.Single(chats);
            Assert.Equal(first, archived.GetProperty("id").GetString());
            Assert.Equal("Trip to Lisbon", archived.GetProperty("title").GetString());
        }
        using (var restored = await User.Http.PostAsync($"/brains/{WorkspaceId}/chats/{first}/unarchive", null, ct))
        { Assert.Equal(HttpStatusCode.NoContent, restored.StatusCode); }
        await Until(view => view.Any(node => node.Action == "chat:" + first) && view.Any(node => node.Action == "chat:" + second), ct);
        using var empty = await User.Http.GetAsync($"/brains/{WorkspaceId}/chats?archived=true", ct);
        Assert.Empty((await empty.Content.ReadFromJsonAsync<JsonElement>(ct)).GetProperty("chats").EnumerateArray());
    }

    [Fact(Timeout = 360_000)]
    public async Task TheChatsModelChoiceIsUsedForItsTurns()
    {
        var ct = TestContext.Current.CancellationToken;
        using var caller = User.AsCaller();
        using var model = Answering();
        await AppRuntimeReadiness.WaitAsync(Brain, User, ct);
        var data = await DataKey(ct);
        await Send("new", "", ct);
        var selection = await TestWait.UntilAsync(async _ => (await Brain.Get<IChat>(data + "/transcript").Read()).Selection,
            current => current is not null && current.Agent.Contains("/conversations/c-", StringComparison.Ordinal), Patience, ct);
        var id = selection!.Agent[(selection.Agent.LastIndexOf('/') + 1)..];

        // The menu lists the catalog: Auto first, then each model; pick an available one other than the default.
        var choices = await Brain.Get<IModelCatalog>(IModelCatalog.Key(WorkspaceId)).List();
        var defaultModel = LLMModel.FindByMarker(typeof(IntoChat.OS.AI.Models.OpenAI.IGpt56Luna))!.Id;
        var chosen = choices.Models.Where(choice => choice.Available).OrderBy(choice => choice.Model == defaultModel ? 1 : 0).First();
        Assert.NotEqual(defaultModel, chosen.Model);
        var menu = await Until(view => view.Any(node => node.Kind == UiNodeKind.Menu && node.Key == "model"), ct);
        var items = menu.Single(node => node.Kind == UiNodeKind.Menu && node.Key == "model").Children;
        Assert.Equal("model:auto", items[0].Action);
        Assert.Contains(items, item => item.Action == "model:" + chosen.Id && item.Enabled);
        await Click("model:" + chosen.Id, ct);
        await TestWait.UntilAsync(_ => Index(data).Get(id), entry => entry?.ModelChoice == chosen.Id, Patience, ct);

        await Send("send", "Summarise my notes", ct);
        var run = await Answered(data, id, ct);
        Assert.Equal(AgentRunStatus.Completed, run.Status);
        Assert.Equal(chosen.Model, run.Model?.Model);
    }

    // Ruling 8: a message that is a whole workspace link posts an Open card and reaches no model.
    [Fact(Timeout = 360_000)]
    public async Task APastedWorkspaceLinkBecomesAnOpenCardAndNoModelTurn()
    {
        var ct = TestContext.Current.CancellationToken;
        using var caller = User.AsCaller();
        using var model = Answering();
        await AppRuntimeReadiness.WaitAsync(Brain, User, ct);
        var data = await DataKey(ct);
        await Send("new", "", ct);
        var agentKey = (await TestWait.UntilAsync(async _ => (await Brain.Get<IChat>(data + "/transcript").Read()).Selection,
            current => current is not null && current.Agent.Contains("/conversations/c-", StringComparison.Ordinal), Patience, ct))!.Agent;
        var asked = Host.Model.Requests(LLMModel.FindByMarker(typeof(IntoChat.OS.AI.Models.OpenAI.IGpt56Luna))!.Id).Count(IsInoTurn);

        await Send("send", "#/open?application=calculator", ct);
        var shown = await TestWait.UntilAsync(_ => Brain.Get<IChat>(data + "/transcript").Read(),
            state => state.Projection?.Messages.Any(message => message.App?.App == "calculator") == true, Patience, ct);
        Assert.Equal("calculator", shown.Projection!.Messages.Single(message => message.App is not null).App!.App);
        var state = await Brain.Get<IAgent>(agentKey).Read();
        var posted = Assert.Single(state.Runs);
        Assert.True(posted.Posted);
        Assert.DoesNotContain("application=", posted.RunId, StringComparison.Ordinal);
        Assert.Equal(asked, Host.Model.Requests(LLMModel.FindByMarker(typeof(IntoChat.OS.AI.Models.OpenAI.IGpt56Luna))!.Id).Count(IsInoTurn));
    }

    // Task 12: a pasted link to an app the brain does not have posts a card that offers Install, which hands the shell
    // Apps' detail for the app (never a one-click install); installing there as the person is the one install path.
    [Fact(Timeout = 360_000)]
    public async Task InoNotInstalledCardInstallsThroughApps()
    {
        var ct = TestContext.Current.CancellationToken;
        using var caller = User.AsCaller();
        using var model = Answering();
        await AppRuntimeReadiness.WaitAsync(Brain, User, ct);
        var data = await DataKey(ct);
        await Send("new", "", ct);
        await TestWait.UntilAsync(async _ => (await Brain.Get<IChat>(data + "/transcript").Read()).Selection,
            current => current is not null && current.Agent.Contains("/conversations/c-", StringComparison.Ordinal), Patience, ct);
        // Calculator is built in: remove this brain's copy so the card sees an app it does not have.
        await Brain.Get<IApp>(IApp.Key(WorkspaceId, "calculator")).Delete();
        Assert.Equal("", (await Brain.Get<IApp>(IApp.Key(WorkspaceId, "calculator")).Read()).Manifest.Title);

        await Send("send", "#/open?application=calculator", ct);
        var shown = await TestWait.UntilAsync(_ => Brain.Get<IChat>(data + "/transcript").Read(),
            state => state.Projection?.Messages.Any(message => message.App?.App == "calculator") == true, Patience, ct);
        var card = shown.Projection!.Messages.Single(message => message.App is not null).App!;
        Assert.False(card.Installed);
        Assert.Equal("os", card.Publisher);

        // What the card's Install does in the shell: open Apps on the app's detail.
        using (var opened = await User.Http.PostAsJsonAsync($"/brains/{WorkspaceId}/apps/apps/request", new { operation = "open", input = $"app:{card.Publisher}/{card.App}" }, ct))
        { opened.EnsureSuccessStatusCode(); }
        var install = await TestWait.UntilAsync(_ => Brain.Get<IButton>(WorkspaceId + "/apps/apps/install").Read(), state => state.Activation is not null, Patience, ct);
        var request = JsonDocument.Parse(install.Activation!).RootElement.GetProperty("request");
        using var installed = await User.Http.PostAsJsonAsync($"/brains/{WorkspaceId}/apps/install", request, ct);
        installed.EnsureSuccessStatusCode();
        Assert.Equal("calculator", (await installed.Content.ReadFromJsonAsync<JsonElement>(ct)).GetProperty("installedName").GetString());
        Assert.NotEqual("", (await Brain.Get<IApp>(IApp.Key(WorkspaceId, "calculator")).Read()).Manifest.Title);
    }

    // Review focus 1: conversations from 3.1.3 (agents with history, no index entries) are all listed after the
    // upgrade's first start, titled by their first message; the selection and the transcripts stay as they were.
    [Fact(Timeout = 360_000)]
    public async Task UpgradeFrom313ListsExistingConversations()
    {
        var ct = TestContext.Current.CancellationToken;
        using var caller = User.AsCaller();
        using var model = Answering();
        await AppRuntimeReadiness.WaitAsync(Brain, User, ct);
        var data = await DataKey(ct);
        // What a 3.1.3 brain holds: messages in main and two c-* conversations, none of the c-* indexed (3.1.3 had no
        // index), and the selection on one of them. Ino 3.2.0 already composed once in this brain, so main has its
        // boot entry; the c-* are the conversations an upgrade would otherwise lose.
        var older = "c-" + Guid.NewGuid().ToString("N");
        var newer = "c-" + Guid.NewGuid().ToString("N");
        await Converse(data + "/conversations/main", "Good morning", ct);
        await Converse(data + "/conversations/" + older, "Draft a letter to my landlord", ct);
        await Converse(data + "/conversations/" + newer, "Compare two phone plans", ct);
        Assert.Null(await Index(data).Get(older));
        Assert.Null(await Index(data).Get(newer));
        var chat = Brain.Get<IChat>(data + "/transcript");
        var current = (await chat.Read()).Selection!;
        await chat.SelectAgent(data + "/conversations/" + older, current.Revision, Guid.NewGuid());
        var transcripts = new Dictionary<string, string>();
        foreach (var id in new[] { "main", older, newer })
        { transcripts[id] = JsonSerializer.Serialize(await Brain.Get<IAgent>(data + "/conversations/" + id).Transcript()); }

        // One restart, nothing selected after it: all three are listed.
        await Restart(ct);
        Assert.Equal("3.3.0", (await Brain.Get<IApp>(InoKey).Read()).Version);
        await Until(view => view.Any(node => node.Action == "chat:main")
            && view.Any(node => node.Action == "chat:" + older && node.Text == "Draft a letter to my landlord")
            && view.Any(node => node.Action == "chat:" + newer && node.Text == "Compare two phone plans"), ct);
        Assert.Equal(data + "/conversations/" + older, (await chat.Read()).Selection!.Agent);
        foreach (var (id, before) in transcripts)
        { Assert.Equal(before, JsonSerializer.Serialize(await Brain.Get<IAgent>(data + "/conversations/" + id).Transcript())); }
    }

    private IChatIndex Index(string data) => Brain.Get<IChatIndex>(data + "/chats");

    // Ino's data key: its ChatDataApp setting (set by the assistant migration) or the app's own key.
    private async Task<string> DataKey(CancellationToken ct)
    {
        var snapshot = await TestWait.UntilAsync(_ => Brain.Get<IApp>(InoKey).Read(), app => app.Status == AppStatus.Live, Patience, ct);
        return snapshot.Settings.GetValueOrDefault("ChatDataApp") is { Length: > 0 } data ? data : InoKey;
    }

    // New chat, then its first message; returns the chat id once the message has started a turn.
    private async Task<string> NewChat(string data, string message, CancellationToken ct)
    {
        var chat = Brain.Get<IChat>(data + "/transcript");
        var before = (await chat.Read()).Selection?.Agent;
        await Send("new", "", ct);
        var selection = await TestWait.UntilAsync(async _ => (await chat.Read()).Selection,
            current => current is not null && current.Agent != before && current.Agent.Contains("/conversations/c-", StringComparison.Ordinal), Patience, ct);
        var id = selection!.Agent[(selection.Agent.LastIndexOf('/') + 1)..];
        await Send("send", message, ct);
        await Answered(data, id, ct);
        return id;
    }

    private async Task<AgentRun> Answered(string data, string id, CancellationToken ct)
    {
        var state = await TestWait.UntilAsync(_ => Brain.Get<IAgent>(data + "/conversations/" + id).Read(),
            agent => agent.ActiveRun is null && agent.Runs.Any(run => !run.Posted && run.Status != AgentRunStatus.Running), Patience, ct);
        return state.Runs.Last(run => !run.Posted);
    }

    // A 3.1.3 conversation: a turn run straight on its agent, which no Ino script indexes.
    private async Task Converse(string agentKey, string message, CancellationToken ct)
    {
        using (User.AsApp(InoKey)) { await Brain.Get<IAgent>(agentKey).Start(AgentTurn.Text(message)); }
        await TestWait.UntilAsync(_ => Brain.Get<IAgent>(agentKey).Read(),
            state => state.ActiveRun is null && state.Runs.Any(run => run.Status == AgentRunStatus.Completed), Patience, ct);
    }

    // A program restart: Configure redeploys Ino, and a new generation of its view is the restart's proof.
    // Not AppRuntimeReadiness: on master, memory capture leaves one TurnCompleted undelivered after Ino's
    // greeting (and after these facts' turns), so the runtime never reports ready again after a restart.
    private async Task Restart(CancellationToken ct)
    {
        var view = Brain.Get<IView>(InoKey + "/view");
        var before = (await view.Read())?.Generation ?? -1;
        await Brain.Get<IApp>(InoKey).Configure(new(), []);
        await TestWait.UntilAsync(_ => view.Read(), published => published is not null && published.Generation > before, Patience, ct);
    }

    private async Task Send(string operation, string input, CancellationToken ct)
    {
        using var sent = await User.Http.PostAsJsonAsync($"/brains/{WorkspaceId}/apps/ino/request", new { operation, input }, ct);
        sent.EnsureSuccessStatusCode();
    }

    private async Task Click(string action, CancellationToken ct)
    {
        var view = Brain.Get<IView>(InoKey + "/view");
        var published = await TestWait.UntilAsync(_ => view.Read(),
            current => current is not null && Flatten(current.Root).Any(node => node.Kind == UiNodeKind.Button && node.Action == action), Patience, ct);
        var button = Flatten(published!.Root).First(node => node.Kind == UiNodeKind.Button && node.Action == action);
        await view.Activate(published.Generation, button.Key, action, Guid.NewGuid());
    }

    private async Task<UiNode[]> Until(Func<UiNode[], bool> ready, CancellationToken ct)
    {
        var view = Brain.Get<IView>(InoKey + "/view");
        try
        {
            var published = await TestWait.UntilAsync(_ => view.Read(), current => current is not null && ready([.. Flatten(current.Root)]), Patience, ct);
            return [.. Flatten(published!.Root)];
        }
        catch (TimeoutException)
        {
            var last = await view.Read();
            throw new TimeoutException("Ino showed: " + string.Join(", ", last is null ? [] : Flatten(last.Root).Select(node => node.Key + "=" + node.Action + "/" + node.Text)));
        }
    }

    internal static IEnumerable<UiNode> Flatten(UiNode root)
    {
        yield return root;
        foreach (var child in root.Children.SelectMany(Flatten)) { yield return child; }
    }

    private static bool IsInoTurn(JsonElement request) => RouterHostFixture.Text(request).Contains(InoPrompt, StringComparison.Ordinal);

    // Jev is unsure, Memory's intend writes a low-confidence brief and Ino answers; restored when disposed.
    private IDisposable Answering()
    {
        var fallback = Host.Model.ChatFallback;
        var route = Host.Model.SystemOneOverride;
        Host.Model.SystemOneOverride = request => RouterHostFixture.Choose(request, "router.ask", "ask", 0.2);
        Host.Model.ChatFallback = request =>
        {
            var text = RouterHostFixture.Text(request);
            if (text.Contains(IntendPrompt, StringComparison.Ordinal))
            {
                return FakeReply.Text(JsonSerializer.Serialize(new
                {
                    brief = new { goal = "Reply to the user's message", inputs = Array.Empty<string>(), constraints = Array.Empty<string>(), acceptance = new[] { "The user has a reply" } },
                    confidence = 0.3,
                    continues = (string?)null,
                }));
            }
            return text.Contains(InoPrompt, StringComparison.Ordinal) ? FakeReply.Text(Answer) : fallback?.Invoke(request) ?? FakeReply.Text(Answer);
        };
        return new Restore(() => { Host.Model.ChatFallback = fallback; Host.Model.SystemOneOverride = route; });
    }

    private sealed class Restore(Action undo) : IDisposable
    {
        public void Dispose() => undo();
    }
}

// Task 5's first run on a brain with no usable model: the deployment holds no provider key and the catalog
// lists a local model (a local desktop run) whose server is not configured, so the card offers Install
// Ollama and Retry, IntoChat Cloud as coming soon, and Add an API key, which opens the Settings window on the keys tab.
[Collection("No model host")]
public sealed class InoFirstRunFacts(NoModelHostFixture host) : BrainFact(host)
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(90);

    private string InoKey => IApp.Key(WorkspaceId, "ino");

    [Fact(Timeout = 360_000)]
    public async Task NoModelShowsTheFirstRunCardInsteadOfAFailure()
    {
        var ct = TestContext.Current.CancellationToken;
        using var caller = User.AsCaller();
        await AppRuntimeReadiness.WaitAsync(Brain, User, ct);
        var snapshot = await Brain.Get<IApp>(InoKey).Read();
        var data = snapshot.Settings.GetValueOrDefault("ChatDataApp") is { Length: > 0 } chatData ? chatData : InoKey;

        using (var opened = await User.Http.PostAsJsonAsync($"/brains/{WorkspaceId}/apps/ino/request", new { operation = "open", input = "" }, ct))
        { opened.EnsureSuccessStatusCode(); }
        var card = await Until(nodes => nodes.Any(node => node.Key == "first-run"), ct);
        var first = card.Single(node => node.Key == "first-run");
        Assert.Equal("How should Ino run?", first.Text);
        var inCard = InoChatFacts.Flatten(first).ToArray();
        Assert.Contains(inCard, node => node.Key == "setup-cloud" && !node.Enabled && node.Text == "IntoChat Cloud");
        Assert.Contains(inCard, node => node.Text == "Coming soon");
        Assert.Contains(inCard, node => node.Key == "setup-keys" && node.Reference?.Name == data + "/setup-keys");
        // "Add an API key" is armed with the shell's settings activation on the keys tab.
        var keys = await TestWait.UntilAsync(_ => Brain.Get<IButton>(data + "/setup-keys").Read(), state => state.Activation is not null, Patience, ct);
        Assert.Equal("Add an API key", keys.Label);
        using (var activation = JsonDocument.Parse(keys.Activation!))
        {
            Assert.Equal(("workspace", "settings", "open", "keys"), (activation.RootElement.GetProperty("kind").GetString(), activation.RootElement.GetProperty("application").GetString(),
                activation.RootElement.GetProperty("operation").GetString(), activation.RootElement.GetProperty("input").GetString()));
        }
        // The local profile lists a local model whose server is not running: Install Ollama and Retry, no "On this computer".
        Assert.DoesNotContain(inCard, node => node.Action == "setup-local");
        Assert.Contains(inCard, node => node.Key == "install-ollama");
        Assert.Contains(inCard, node => node.Action == "setup-retry");
        var ollama = await TestWait.UntilAsync(_ => Brain.Get<IButton>(data + "/install-ollama").Read(), state => state.Activation is not null, Patience, ct);
        Assert.Contains("https://ollama.com/download", ollama.Activation, StringComparison.Ordinal);

        // Sending shows the card again and records no failed turn.
        using (var sent = await User.Http.PostAsJsonAsync($"/brains/{WorkspaceId}/apps/ino/request", new { operation = "send", input = "hello" }, ct))
        { sent.EnsureSuccessStatusCode(); }
        var agentKey = (await Brain.Get<IChat>(data + "/transcript").Read()).Selection!.Agent;
        await TestWait.UntilAsync(_ => Brain.Get<IComposer>(data + "/composer").Read(), state => state.Draft == "hello", Patience, ct);
        Assert.DoesNotContain((await Brain.Get<IAgent>(agentKey).Read()).Runs, run => !run.Posted);
        Assert.DoesNotContain((await Brain.Get<IChat>(data + "/transcript").Read()).Projection?.Messages ?? [], message => message.Status == "failed");

        // Retry marks the view dirty (view-state), so view.cs republishes from a fresh catalog read; still with
        // nothing usable, the card stays.
        var view = Brain.Get<IView>(InoKey + "/view");
        var viewState = Brain.Get<IntoChat.OS.Flutter.Text.IText>(data + "/view-state");
        var dirty = (await viewState.Read()).Version;
        var published = (await view.Read())!;
        var retry = InoChatFacts.Flatten(published.Root).First(node => node.Action == "setup-retry");
        await view.Activate(published.Generation, retry.Key, "setup-retry", Guid.NewGuid());
        await TestWait.UntilAsync(_ => viewState.Read(), state => state.Version > dirty, Patience, ct);
        await Until(nodes => nodes.Any(node => node.Key == "first-run") && nodes.Any(node => node.Action == "setup-retry"), ct);
    }

    private async Task<UiNode[]> Until(Func<UiNode[], bool> ready, CancellationToken ct)
    {
        var view = Brain.Get<IView>(InoKey + "/view");
        try
        {
            var published = await TestWait.UntilAsync(_ => view.Read(), current => current is not null && ready([.. InoChatFacts.Flatten(current.Root)]), Patience, ct);
            return [.. InoChatFacts.Flatten(published!.Root)];
        }
        catch (TimeoutException)
        {
            var last = await view.Read();
            var app = await Brain.Get<IApp>(InoKey).Read();
            var runtime = await Brain.Get<IntoChat.OS.Microsoft.CSharp.Runtime.IBrainRuntime>(IntoChat.OS.Microsoft.CSharp.Runtime.BrainRuntimeKeys.For(WorkspaceId)).Inspect(ct);
            var behaviors = string.Join(" || ", runtime.Behaviors.Where(behavior => behavior.Id.Contains("ino", StringComparison.OrdinalIgnoreCase))
                .Select(behavior => $"{behavior.Id} [{behavior.Status}] {behavior.Error} runs {behavior.Runs}"));
            throw new TimeoutException($"{behaviors} || Ino {app.Version} {app.Status} error {app.Presentation?.Error} activity {string.Join(" | ", app.Activity.TakeLast(8))} showed: " + string.Join(", ", last is null ? [] : InoChatFacts.Flatten(last.Root).Select(node => node.Key + "=" + node.Action + "/" + node.Text)));
        }
    }
}
