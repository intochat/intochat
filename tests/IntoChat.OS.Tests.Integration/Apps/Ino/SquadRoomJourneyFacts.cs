using System.Diagnostics;
using DigitalBrain;
using DigitalBrain.Runtime.Orleans.Hosting;
using DigitalBrain.UI;
using DigitalBrain.UI.Commands;
using IntoChat.OS.Apps.AgentApp;
using IntoChat.OS.Apps.Ino;
using IntoChat.OS.Host.Kernel;
using IntoChat.OS.Host.Primitives;
using IntoChat.OS.Modules.AI.Agents;
using IntoChat.OS.Modules.Apps;
using IntoChat.OS.Modules.Apps.Tasks;
using IntoChat.OS.Modules.Flutter.Conversations;
using IntoChat.OS.Modules.UI.Controls;
using Microsoft.Extensions.AI;
using TaskStatus = IntoChat.OS.Modules.Apps.Tasks.TaskStatus;

namespace IntoChat.OS.Apps.Tests.Integration.Ino;

[Collection(nameof(InoSiloCollection))]
public sealed class SquadRoomJourneyFacts(InoSiloFixture fixture)
{
    private static readonly TimeSpan Within = TimeSpan.FromSeconds(15);

    [Fact]
    public async Task SquadRoom_RealSiloJourney_RoutesLeadAndMentions_CapsDelegationAtThree_AndSharesKanbanAndCheckBoxGrains()
    {
        var ct = TestContext.Current.CancellationToken;
        var brain = fixture.NewBrain("squad");
        var brainId = new BrainId(brain);
        using var person = fixture.AsPerson(brain);

        fixture.Model.ClearRequests();

        // Create two Rung-1 custom specialist bots (@alpha and @beta) with IsolatedBotScope.
        var alphaApp = SiloNeurons.Get<IApp>(fixture.Services, new NeuronId(brainId, AppGrainType.Name, "apps/alpha"));
        await alphaApp.Create(
            new AppManifest(
                Title: "Alpha Architect",
                Description: "Architecture specialist.",
                Operations: [],
                Settings: [],
                Slots: [],
                Agent: new AppAgent(
                    Instructions: "ALPHA_SILO_AGENT",
                    Handle: "@alpha",
                    AllowedContracts: [typeof(ITasks).FullName!],
                    MemoryScope: MemoryScope.IsolatedBotScope)),
            "");

        var betaApp = SiloNeurons.Get<IApp>(fixture.Services, new NeuronId(brainId, AppGrainType.Name, "apps/beta"));
        await betaApp.Create(
            new AppManifest(
                Title: "Beta Coder",
                Description: "Implementation specialist.",
                Operations: [],
                Settings: [],
                Slots: [],
                Agent: new AppAgent(
                    Instructions: "BETA_SILO_AGENT",
                    Handle: "@beta",
                    AllowedContracts: [typeof(ITasks).FullName!],
                    MemoryScope: MemoryScope.IsolatedBotScope)),
            "");

        // Create Squad Room via InoApp (@ino Lead + @alpha + @beta).
        var ino = SiloNeurons.Get<IScreen>(fixture.Services, new NeuronId(brainId, "app.ino", "default"));
        var createSquadArgs = DigitalBrain.Invocation.RuntimeProtocol.SerializeToElement(
            new InoCreateSquad("Wave 3 Squad", ["@ino", "@alpha", "@beta"], LeadAgentId: "@ino"),
            typeof(InoCreateSquad));
        await ino.Dispatch(new Command(".", "ino.new-squad", createSquadArgs));

        // Discover the created Squad conversation from IThreadDirectory.
        var directory = SiloNeurons.Get<IThreadDirectory>(fixture.Services, new NeuronId(brainId, Threads.Type, IThreadDirectory.DefaultKey));
        var page = await directory.List();
        var squadEntry = Assert.Single(page.Entries, e => e.Kind == ThreadKind.Squad && e.Title == "Wave 3 Squad");
        Assert.Equal(ThreadTrustPosture.OwnerTrusted, squadEntry.TrustPosture);

        var conv = SiloNeurons.Get<IConversation>(fixture.Services, new NeuronId(brainId, IConversation.Type, IConversation.KeyOf(ConversationId.Parse(squadEntry.Id))));
        var initialView = await conv.Read();
        Assert.Equal(ThreadTrustPosture.OwnerTrusted, initialView.TrustPosture);
        Assert.Equal(ThreadKind.Squad, initialView.Kind);
        Assert.Equal(3, initialView.SeatedAgents.Length);
        Assert.Contains(initialView.SeatedAgents, a => a.AgentId == "ino" && a.IsLead);
        Assert.Contains(initialView.SeatedAgents, a => a.AgentId == "agent-alpha" && a.Handle == "@alpha");
        Assert.Contains(initialView.SeatedAgents, a => a.AgentId == "agent-beta" && a.Handle == "@beta");

        // 1. Unmentioned human turn routes to @ino Lead.
        fixture.Model.Reply("Ino lead reporting ready.");
        await conv.Say("Give me a quick squad kickoff.");
        var afterLead = await WaitForAssistantCountAsync(conv, 1, ct);
        var leadReply = afterLead.Entries.OfType<Said>().Last(static s => s.By is Participant.Assistant);
        Assert.Equal("ino", ((Participant.Assistant)leadReply.By).AgentId);

        // 2. Explicit @alpha mention routes to @alpha, which delegates @beta -> @alpha -> @beta and halts cleanly at maxDepth = 3!
        fixture.Model.Respond((messages, options, _) =>
        {
            var sys = (options?.Instructions ?? "") + "\n" + string.Concat(messages.Where(m => m.Role == ChatRole.System).Select(m => m.Text));
            var text = sys.Contains("ALPHA_SILO_AGENT", StringComparison.Ordinal)
                ? "Alpha working, handing off to @beta"
                : "Beta working, handing off to @alpha";
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, text)));
        });

        await conv.Say("@alpha coordinate the rollout");
        var afterDelegation = await WaitForAssistantCountAsync(conv, 4, ct);
        var squadReplies = afterDelegation.Entries
            .OfType<Said>()
            .Where(static s => s.By is Participant.Assistant)
            .Select(static s => ((Participant.Assistant)s.By).AgentId)
            .ToArray();

        Assert.Equal(["ino", "agent-alpha", "agent-beta", "agent-alpha"], squadReplies);
        Assert.False(string.IsNullOrWhiteSpace(afterDelegation.HandoffLimitNotice));
        Assert.Contains("maxDepth = 3", afterDelegation.HandoffLimitNotice!, StringComparison.Ordinal);

        // 3. Human & Agent both mutate the exact same server-side KanbanNeuron & CheckBoxNeuron grains and observe uniform state.
        var tasks = SiloNeurons.Get<ITasks>(fixture.Services, ITasks.Key(brainId));
        var createdTask = await tasks.Create("Ship Squad Plan", "Verify shared board", tag: "Squad", conversationId: squadEntry.Id);

        // Open Squad Plan Drawer in InoApp (mounts KanbanNeuron & CheckBoxNeuron child grains).
        await ino.Dispatch(new Command(".", "ino.open-squad-plan"));

        // Human drags card on KanbanNeuron to "in-progress".
        var moveArgs = DigitalBrain.Invocation.RuntimeProtocol.SerializeToElement(
            new InoMoveSquadTask(createdTask.TaskId, "in-progress", 0),
            typeof(InoMoveSquadTask));
        await ino.Dispatch(new Command(".", "ino.squad-move-task", moveArgs));

        var afterHumanMove = await WaitUntilAsync(
            async () =>
            {
                var r = await tasks.Read(createdTask.TaskId);
                return r?.Status == TaskStatus.InProgress ? r : null;
            },
            Within,
            ct);
        Assert.Equal(TaskStatus.InProgress, afterHumanMove.Status);

        // Seated agent mutates the task via ITasks.Transition -> Completed, and InoApp's KanbanNeuron + CheckBoxNeuron child props reflect Done / IsOn = true!
        await tasks.Transition(createdTask.TaskId, TaskStatus.Completed, "Completed by @beta");

        var inoViewJson = await WaitUntilAsync(
            async () =>
            {
                var published = await ino.Read();
                var raw = published?.View.GetRawText();
                return raw is not null
                    && raw.Contains(InoSquads.CheckKey(createdTask.TaskId), StringComparison.Ordinal)
                    && raw.Contains("\"isOn\":true", StringComparison.OrdinalIgnoreCase)
                    && raw.Contains("\"columnId\":\"done\"", StringComparison.OrdinalIgnoreCase)
                    ? raw
                    : null;
            },
            Within,
            ct);
        Assert.Contains(InoSquads.KanbanKey, inoViewJson, StringComparison.Ordinal);
        Assert.Contains("\"isOn\":true", inoViewJson, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("\"columnId\":\"done\"", inoViewJson, StringComparison.OrdinalIgnoreCase);

        // Human unchecks the CheckBoxNeuron grain -> task transitions back to InProgress uniformly!
        var toggleArgs = DigitalBrain.Invocation.RuntimeProtocol.SerializeToElement(
            new InoToggleSquadTask(createdTask.TaskId, Checked: false),
            typeof(InoToggleSquadTask));
        await ino.Dispatch(new Command(".", "ino.squad-toggle-task", toggleArgs));

        var afterHumanUncheck = await WaitUntilAsync(
            async () =>
            {
                var r = await tasks.Read(createdTask.TaskId);
                return r?.Status == TaskStatus.InProgress ? r : null;
            },
            Within,
            ct);
        Assert.Equal(TaskStatus.InProgress, afterHumanUncheck.Status);
    }

    private static async Task<ConversationView> WaitForAssistantCountAsync(IConversation conv, int expectedCount, CancellationToken ct)
        => await WaitUntilAsync(
            async () =>
            {
                var view = await conv.Read();
                var count = view.Entries.OfType<Said>().Count(static s => s.By is Participant.Assistant);
                return view.Active is null && count >= expectedCount ? view : null;
            },
            Within,
            ct);

    private static async Task<T> WaitUntilAsync<T>(Func<Task<T?>> probe, TimeSpan timeout, CancellationToken ct)
        where T : class
    {
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < timeout)
        {
            if (await probe() is { } result)
            {
                return result;
            }
            await Task.Delay(50, ct);
        }

        throw new TimeoutException($"Condition was not met within {timeout.TotalSeconds:F1}s.");
    }
}
