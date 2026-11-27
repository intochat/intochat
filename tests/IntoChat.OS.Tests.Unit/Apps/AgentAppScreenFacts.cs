using System.Text.Json;
using DigitalBrain;
using DigitalBrain.Neurons;
using DigitalBrain.Testing;
using DigitalBrain.UI;
using DigitalBrain.UI.Controls;
using IntoChat.OS.Apps.AgentApp;
using IntoChat.OS.Modules.UI.Controls;
using IntoChat.OS.Host.Primitives;
using IntoChat.OS.Modules.AI.Agents;
using IntoChat.OS.Modules.AI.Models;
using IntoChat.OS.Modules.Apps;
using IntoChat.OS.Modules.Flutter.Conversations;
using IntoChat.OS.Modules.Identity.Contracts;
using Microsoft.Extensions.DependencyInjection;

namespace IntoChat.OS.Tests.Unit.Apps;

public sealed class AgentAppScreenFacts
{
    private static readonly BrainId Brain = new("local");

    [Neuron(IConversation.Type)]
    private sealed class RecordingConversation : Neuron, IConversation
    {
        private readonly List<ConversationEntry> _entries = [];
        private readonly List<AgentRun> _runs = [];
        private long _revision;

        public static List<(string AgentId, AgentDefinition Definition)> SeatedCalls { get; } = [];
        public static List<string> SaidTexts { get; } = [];

        public static void Reset()
        {
            SeatedCalls.Clear();
            SaidTexts.Clear();
        }

        public Task Seat(string agentId, AgentDefinition definition)
        {
            SeatedCalls.Add((agentId, definition));
            return Task.CompletedTask;
        }

        public async Task Say(string text, ModelId? model = null, AttachmentRef[]? attachments = null)
        {
            SaidTexts.Add(text);
            var runId = $"r{_runs.Count + 1}";
            var now = DateTimeOffset.UtcNow;
            var userEntry = new Said(EntryIds.Said(runId), new Participant.Human(new AccountId("local")), now, text);
            var replyEntry = new Said(EntryIds.Reply(runId), new Participant.Assistant("agent-treg"), now.AddMilliseconds(5), $"Echo: {text}");
            _entries.Add(userEntry);
            _entries.Add(replyEntry);
            _runs.Add(new AgentRun(runId, AgentRunStatus.Completed, text, now, now.AddMilliseconds(5), $"Echo: {text}"));
            _revision++;
            await Publish(new ConversationChanged(ConversationId.Parse(Id.Key["conversations/".Length..]), _revision));
        }

        public Task Stop() => Task.CompletedTask;
        public Task Regenerate(EntryId reply) => Task.CompletedTask;
        public Task Invite(AccountId friend) => Task.CompletedTask;
        public Task Post(string text, string id) => Task.CompletedTask;
        public Task Voiced(string heard, string reply, ModelId model, string id) => Task.CompletedTask;

        public Task<ConversationView> Read(long? sinceRevision = null)
        {
            var convId = ConversationId.Parse(Id.Key["conversations/".Length..]);
            return Task.FromResult(new ConversationView(
                convId,
                _revision,
                [],
                [.. _entries],
                null,
                [.. _runs],
                false,
                []));
        }
    }

    [Fact]
    public async Task AgentAppScreenRendersStartersSeatsIsolatedDefinitionAndChatsThroughChatThread()
    {
        RecordingConversation.Reset();

        var services = new ServiceCollection();
        services.AddOptions<AppsOptions>();
        services.AddSingleton(TimeProvider.System);
        await using var os = TestOS.Local(o => o.Services = services.BuildServiceProvider());
        os.Register<AppNeuron>();
        os.Register<RecordingConversation>();
        os.Register<ChatThreadNeuron>();
        os.Register<AgentAppScreen>();

        using var person = os.Edge.Enter(os.Edge.Person("local", "local", Brain));

        var app = os.Get<IApp>(new NeuronId(Brain, AppGrainType.Name, "apps/treg"));
        var manifest = new AppManifest(
            Title: "Treg Assistant",
            Description: "Scoped treg chat",
            Operations: [],
            Settings: [],
            Slots: [new AppSlot("treg", Connector: "mcp", Required: true)],
            Agent: new AppAgent(
                Instructions: "Answer only using treg.",
                Starters: ["Show open bugs"],
                Tools: ["treg"]));

        var created = await app.Create(manifest, "");
        Assert.Equal(AppStatus.Live, created.Status);

        const string tregKey = "local/connection/mcp/treg-main";
        var bound = await app.BindSlots(
            new Dictionary<string, string>(StringComparer.Ordinal) { ["treg"] = tregKey },
            TestContext.Current.CancellationToken);
        Assert.Equal(SlotBindingStatus.Ok, bound.Status);

        var screenId = new NeuronId(Brain, "agentapp", "treg");
        var screen = os.Get<IScreen>(screenId);
        var published = await screen.Read();

        Assert.NotNull(published);
        Assert.Equal("agentapp", published.Alias);
        var rawView = published.View.GetRawText();
        Assert.Contains("Treg Assistant", rawView, StringComparison.Ordinal);
        Assert.Contains("Show open bugs", rawView, StringComparison.Ordinal);

        // Verify isolated AgentDefinition was seated on agent-treg conversation.
        var seated = Assert.Single(RecordingConversation.SeatedCalls);
        Assert.Equal("agent-treg", seated.AgentId);
        Assert.Equal("Answer only using treg.", seated.Definition.Instructions);
        Assert.Empty(seated.Definition.Contracts);
        Assert.False(seated.Definition.IncludeComponentOperations);
        Assert.Equal([tregKey], seated.Definition.ToolSources ?? []);

        // Click starter button via IScreen.Activate -> triggers Say -> projects reply into ChatThreadNeuron.
        await screen.Click("starter:0");

        Assert.Equal(["Show open bugs"], RecordingConversation.SaidTexts);

        var childView = await screen.ReadChild(AgentAppScreen.ConversationSlotKey);
        Assert.NotNull(childView);
        var threadView = Assert.IsType<ChatThreadView>(
            childView.View.GetProperty("root").Deserialize<Node>(JsonSerializerOptions.Web));
        Assert.Equal(2, threadView.Messages.Count);
        Assert.Equal("Show open bugs", threadView.Messages[0].Text);
        Assert.Equal("Echo: Show open bugs", threadView.Messages[1].Text);
    }
}
