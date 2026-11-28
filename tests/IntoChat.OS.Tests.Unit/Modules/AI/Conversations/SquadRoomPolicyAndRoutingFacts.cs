using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using DigitalBrain;
using DigitalBrain.Capabilities.Connections.Integrations;
using DigitalBrain.Capabilities.Secrets;
using DigitalBrain.Runtime.Http.Surface;
using DigitalBrain.Testing;
using IntoChat.OS.Apps.AgentApp;
using IntoChat.OS.Apps.Ino;
using IntoChat.OS.Modules.AI;
using IntoChat.OS.Modules.AI.Agents;
using IntoChat.OS.Modules.AI.Conversations;
using IntoChat.OS.Modules.AI.Memory;
using IntoChat.OS.Modules.AI.Models;
using IntoChat.OS.Modules.Apps;
using IntoChat.OS.Modules.Flutter.Conversations;
using IntoChat.OS.Modules.Identity.Contracts;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;

namespace IntoChat.OS.Tests.Unit.Modules.AI.Conversations;

public sealed class SquadRoomPolicyAndRoutingFacts
{
    private static readonly BrainId OwnerBrain = new("owner");
    private static readonly BrainId FriendBrain = new("friend-brain");

    private sealed class NoOpSecretRedactor : ISecretRedactor
    {
        public ValueTask<string?> Redact(string? text, CancellationToken cancellationToken) => ValueTask.FromResult(text);
        public ValueTask<int> TailLength(CancellationToken cancellationToken) => ValueTask.FromResult(0);
    }

    private sealed class NoOpAiCredentials : IAiCredentials
    {
        public Task<IntegrationSnapshot> StatusOf(string integrationId, CancellationToken cancellationToken = default)
            => Task.FromResult(new IntegrationSnapshot { IntegrationId = integrationId, Status = IntegrationStatus.Unconfigured });
        public Task<long> GenerationOf(string integrationId, CancellationToken cancellationToken = default) => Task.FromResult(0L);
        public Task<ReleasedIntegration> MaterializeAsync(string integrationId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<string> ReleaseSecretAsync(string integrationId, string field, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<string?> BrainKey(string brainId, AiProvider provider, CancellationToken cancellationToken = default) => Task.FromResult<string?>(null);
    }

    private sealed class ScriptedChatClient(Func<IEnumerable<ChatMessage>, ChatOptions?, string> responder) : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
            => Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, responder(messages, options))));

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            var text = responder(messages, options);
            yield return new ChatResponseUpdate(ChatRole.Assistant, text);
            await Task.CompletedTask;
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    private static IServiceProvider CreateServices(IChatClient client)
    {
        var sc = new ServiceCollection()
            .AddSingleton<ISecretRedactor>(new NoOpSecretRedactor())
            .AddSingleton<IAiCredentials>(new NoOpAiCredentials())
            .AddSingleton(new NeuronSurface())
            .AddSingleton<IChatClient>(client)
            .AddSingleton<ModelCatalog>()
            .AddSingleton<IMemoryEmbedder>(new HybridMemoryEmbedder())
            .AddSingleton(TimeProvider.System);
        sc.AddOptions<AIOptions>();
        return sc.BuildServiceProvider();
    }

    [Fact]
    public void PrepareCreate_ValidatesTwoToNSeatedAgents_DefaultLead_AndRejectsExternalAccounts()
    {
        // Valid: 2..N agents with default @ino lead.
        var ok = InoSquads.PrepareCreate("Release Squad", ["@ino", "@architect", "@coder", "@qa"]);
        Assert.True(ok.Succeeded, ok.Error);
        Assert.NotNull(ok.Plan);
        Assert.Equal("Release Squad", ok.Plan!.Title);
        Assert.Equal(["ino", "architect", "coder", "qa"], ok.Plan.SeatedAgentIds);
        Assert.Equal("ino", ok.Plan.LeadAgentId);

        // Configurable lead.
        var customLead = InoSquads.PrepareCreate("Arch Squad", ["@ino", "@architect"], rawLeadAgentId: "@architect");
        Assert.True(customLead.Succeeded, customLead.Error);
        Assert.Equal("architect", customLead.Plan!.LeadAgentId);

        // Reject < 2 seated agents.
        var tooFew = InoSquads.PrepareCreate("Solo", ["@ino"]);
        Assert.False(tooFew.Succeeded);
        Assert.Contains("at least 2", tooFew.Error!, StringComparison.OrdinalIgnoreCase);

        // Reject external friends explicitly passed.
        var extParam = InoSquads.PrepareCreate("Leak Squad", ["@ino", "@architect"], externalAccounts: ["alice@example.com"]);
        Assert.False(extParam.Succeeded);
        Assert.Contains("OwnerTrusted", extParam.Error!, StringComparison.OrdinalIgnoreCase);

        // Reject external friend / cross-brain account disguised as seated agent.
        var extAgent = InoSquads.PrepareCreate("Leak Squad", ["@ino", "friend:alice"]);
        Assert.False(extAgent.Succeeded);
        Assert.Contains("OwnerTrusted", extAgent.Error!, StringComparison.OrdinalIgnoreCase);

        var extEmail = InoSquads.PrepareCreate("Leak Squad", ["@ino", "alice@external.org"]);
        Assert.False(extEmail.Succeeded);
        Assert.Contains("OwnerTrusted", extEmail.Error!, StringComparison.OrdinalIgnoreCase);

        // Reject unknown lead not in seated agents.
        var badLead = InoSquads.PrepareCreate("Squad", ["@ino", "@architect"], rawLeadAgentId: "@qa");
        Assert.False(badLead.Succeeded);

        // Reject traversal segments and control chars in agent tokens.
        Assert.Equal(string.Empty, InoSquads.NormalizeAgentToken(".."));
        Assert.Equal(string.Empty, InoSquads.NormalizeAgentToken("."));
        Assert.Equal(string.Empty, InoSquads.NormalizeAgentToken("a..b"));
        Assert.Equal(string.Empty, InoSquads.NormalizeAgentToken("alpha\0beta"));
    }

    [Fact]
    public async Task SquadRoom_RejectsExternalFriendInvite_AndPreservesImmutableOwnerTrustedPosture()
    {
        var services = CreateServices(new ScriptedChatClient((_, _) => "ok"));
        await using var os = TestOS.Local(options => options.Services = services)
            .Register<ConversationNeuron>();

        var convId = new NeuronId(OwnerBrain, IConversation.Type, "conversations/squad-sec");
        using (os.Edge.Enter(os.Edge.Person("owner", "owner", OwnerBrain)))
        {
            var conv = os.Get<IConversation>(convId);
            await conv.Configure(ThreadTrustPosture.OwnerTrusted, ThreadKind.Squad, "Private Squad", leadAgentId: "ino");
            await conv.Seat("ino", new AgentDefinition { DisplayName = "Ino", Handle = "@ino" });
            await conv.Seat("agent-architect", new AgentDefinition { DisplayName = "Architect", Handle = "@architect" });

            // External friends can NEVER be invited into an OwnerTrusted Squad Room.
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
                conv.Invite(new AccountId("alice"), FriendBrain));

            // Immutable ThreadTrustPosture cannot be flipped to CrossBrainShared.
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                conv.Configure(ThreadTrustPosture.CrossBrainShared, ThreadKind.SharedRoom, "Hijacked"));

            var view = await conv.Read();
            Assert.Equal(ThreadTrustPosture.OwnerTrusted, view.TrustPosture);
            Assert.Equal(ThreadKind.Squad, view.Kind);
            Assert.Equal(2, view.SeatedAgents.Length);
            Assert.Contains(view.SeatedAgents, a => a.AgentId == "ino" && a.IsLead);
            Assert.Contains(view.SeatedAgents, a => a.AgentId == "agent-architect" && a.Handle == "@architect" && !a.IsLead);
        }
    }

    [Fact]
    public async Task SquadRouting_SupportsMultiMentionOrder_IgnoresUnseated_AndFallsBackToLead()
    {
        var client = new ScriptedChatClient((messages, options) =>
        {
            var sys = (options?.Instructions ?? "") + "\n" + string.Concat(messages.Where(m => m.Role == ChatRole.System).Select(m => m.Text));
            if (sys.Contains("ALPHA_PERSONA", StringComparison.Ordinal)) { return "Alpha reviewed."; }
            if (sys.Contains("BETA_PERSONA", StringComparison.Ordinal)) { return "Beta tested."; }
            return "Ino lead responded.";
        });

        var services = CreateServices(client);
        await using var os = TestOS.Local(options => options.Services = services)
            .Register<ConversationNeuron>();

        var convId = new NeuronId(OwnerBrain, IConversation.Type, "conversations/squad-routing");
        using (os.Edge.Enter(os.Edge.Person("owner", "owner", OwnerBrain)))
        {
            var conv = os.Get<IConversation>(convId);
            await conv.Configure(ThreadTrustPosture.OwnerTrusted, ThreadKind.Squad, "Delivery Squad", leadAgentId: "ino");
            await conv.Seat("ino", new AgentDefinition { DisplayName = "Ino", Handle = "@ino", Instructions = "INO_PERSONA" });
            await conv.Seat("agent-alpha", new AgentDefinition { DisplayName = "Alpha", Handle = "@alpha", Instructions = "ALPHA_PERSONA" });
            await conv.Seat("agent-beta", new AgentDefinition { DisplayName = "Beta", Handle = "@beta", Instructions = "BETA_PERSONA" });

            // 1. Unmentioned turn routes to LeadAgentId (@ino).
            await conv.Say("What is our release status?");
            await os.Idle();

            // 2. Unseated mention safely falls back to LeadAgentId (@ino).
            await conv.Say("@ghost please check this");
            await os.Idle();

            // 3. Multi-mention routes in exact mention order (@beta then @alpha), ignoring @unknown.
            await conv.Say("@unknown @beta verify build then @alpha approve architecture");
            await os.Idle();

            var view = await conv.Read();
            var assistantReplies = view.Entries
                .OfType<Said>()
                .Where(static s => s.By is Participant.Assistant)
                .Select(static s => (((Participant.Assistant)s.By).AgentId, s.Text))
                .ToArray();

            Assert.Equal(4, assistantReplies.Length);
            Assert.Equal(("ino", "Ino lead responded."), assistantReplies[0]);
            Assert.Equal(("ino", "Ino lead responded."), assistantReplies[1]);
            Assert.Equal(("agent-beta", "Beta tested."), assistantReplies[2]);
            Assert.Equal(("agent-alpha", "Alpha reviewed."), assistantReplies[3]);
        }
    }

    [Fact]
    public async Task SquadDelegation_HaltsAtMaxDepthThree_AndSurfacesCleanHandoffLimitSummary()
    {
        var client = new ScriptedChatClient((messages, options) =>
        {
            var sys = (options?.Instructions ?? "") + "\n" + string.Concat(messages.Where(m => m.Role == ChatRole.System).Select(m => m.Text));
            return sys.Contains("ALPHA_PERSONA", StringComparison.Ordinal)
                ? "Alpha delegating to @beta"
                : "Beta delegating to @alpha";
        });

        var services = CreateServices(client);
        await using var os = TestOS.Local(options => options.Services = services)
            .Register<ConversationNeuron>();

        var convId = new NeuronId(OwnerBrain, IConversation.Type, "conversations/squad-depth");
        using (os.Edge.Enter(os.Edge.Person("owner", "owner", OwnerBrain)))
        {
            var conv = os.Get<IConversation>(convId);
            await conv.Configure(ThreadTrustPosture.OwnerTrusted, ThreadKind.Squad, "Loop Guard Squad", leadAgentId: "ino");
            await conv.Seat("ino", new AgentDefinition { DisplayName = "Ino", Handle = "@ino", Instructions = "INO_PERSONA" });
            await conv.Seat("agent-alpha", new AgentDefinition { DisplayName = "Alpha", Handle = "@alpha", Instructions = "ALPHA_PERSONA" });
            await conv.Seat("agent-beta", new AgentDefinition { DisplayName = "Beta", Handle = "@beta", Instructions = "BETA_PERSONA" });

            await conv.Say("@alpha start ping pong");
            await os.Idle();

            var view = await conv.Read();
            var replies = view.Entries.OfType<Said>().Where(static s => s.By is Participant.Assistant).ToArray();
            Assert.Equal(SharedRoomAgentPolicy.MaxSquadHandoffDepth, replies.Length);
            Assert.Equal(["agent-alpha", "agent-beta", "agent-alpha"], replies.Select(static r => ((Participant.Assistant)r.By).AgentId));

            // Clean handoff-limit summary is surfaced on ConversationView and recorded in log.
            Assert.False(string.IsNullOrWhiteSpace(view.HandoffLimitNotice));
            Assert.Contains("maxDepth = 3", view.HandoffLimitNotice!, StringComparison.Ordinal);
            Assert.Contains("@alpha", view.HandoffLimitNotice!, StringComparison.Ordinal);
            Assert.Contains("@beta", view.HandoffLimitNotice!, StringComparison.Ordinal);
            Assert.Contains(view.Entries.OfType<Summarized>(), s => s.Summary.Contains("maxDepth = 3", StringComparison.Ordinal));
        }
    }

    [Fact]
    public async Task SquadDelegation_CumulativeMaxDepthThree_CapsCombinedMultiMentionAndHandoffs()
    {
        var client = new ScriptedChatClient((messages, options) =>
        {
            var sys = (options?.Instructions ?? "") + "\n" + string.Concat(messages.Where(m => m.Role == ChatRole.System).Select(m => m.Text));
            if (sys.Contains("ALPHA_PERSONA", StringComparison.Ordinal)) { return "Alpha done, handing off to @beta"; }
            if (sys.Contains("BETA_PERSONA", StringComparison.Ordinal)) { return "Beta done, handing off to @gamma"; }
            if (sys.Contains("GAMMA_PERSONA", StringComparison.Ordinal)) { return "Gamma done."; }
            return "Ino done.";
        });

        var services = CreateServices(client);
        await using var os = TestOS.Local(options => options.Services = services)
            .Register<ConversationNeuron>();

        var convId = new NeuronId(OwnerBrain, IConversation.Type, "conversations/squad-cumulative-depth");
        using (os.Edge.Enter(os.Edge.Person("owner", "owner", OwnerBrain)))
        {
            var conv = os.Get<IConversation>(convId);
            await conv.Configure(ThreadTrustPosture.OwnerTrusted, ThreadKind.Squad, "Cumulative Guard Squad", leadAgentId: "ino");
            await conv.Seat("ino", new AgentDefinition { DisplayName = "Ino", Handle = "@ino", Instructions = "INO_PERSONA" });
            await conv.Seat("agent-alpha", new AgentDefinition { DisplayName = "Alpha", Handle = "@alpha", Instructions = "ALPHA_PERSONA" });
            await conv.Seat("agent-beta", new AgentDefinition { DisplayName = "Beta", Handle = "@beta", Instructions = "BETA_PERSONA" });
            await conv.Seat("agent-gamma", new AgentDefinition { DisplayName = "Gamma", Handle = "@gamma", Instructions = "GAMMA_PERSONA" });

            // Human mentions 4 agents while @alpha -> @beta -> @gamma also chain handoffs.
            // Cumulative budget must cap total turns at MaxSquadHandoffDepth = 3 (never 3*K) and surface HandoffLimitNotice.
            await conv.Say("@alpha @beta @gamma @ino coordinate the launch");
            await os.Idle();

            var view = await conv.Read();
            var replies = view.Entries.OfType<Said>().Where(static s => s.By is Participant.Assistant).ToArray();
            Assert.Equal(SharedRoomAgentPolicy.MaxSquadHandoffDepth, replies.Length);
            Assert.Equal(["agent-alpha", "agent-beta", "agent-gamma"], replies.Select(static r => ((Participant.Assistant)r.By).AgentId));
            Assert.False(string.IsNullOrWhiteSpace(view.HandoffLimitNotice));
            Assert.Contains("maxDepth = 3", view.HandoffLimitNotice!, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task SquadSpecialists_PreservePerBotIsolatedMemoryScope_AndAgentAppPolicy()
    {
        var seenSystemPrompts = new ConcurrentBag<string>();
        var client = new ScriptedChatClient((messages, options) =>
        {
            var sys = (options?.Instructions ?? "") + "\n" + string.Concat(messages.Where(m => m.Role == ChatRole.System).Select(m => m.Text));
            seenSystemPrompts.Add(sys);
            return sys.Contains("ALPHA_BOT", StringComparison.Ordinal) ? "Alpha isolated reply." : "Beta isolated reply.";
        });

        var services = CreateServices(client);
        await using var os = TestOS.Local(options => options.Services = services)
            .Register(typeof(ConversationNeuron).Assembly);

        var alphaManifest = new AppManifest(
            Title: "Alpha Specialist",
            Description: "Alpha bot",
            Operations: [],
            Settings: [],
            Slots: [],
            Agent: new AppAgent(
                Instructions: "ALPHA_BOT",
                Handle: "@alpha",
                AllowedContracts: [typeof(IntoChat.OS.Modules.Apps.Tasks.ITasks).FullName!],
                ToolSources: [$"{OwnerBrain.Value}/connection/mcp/alpha-tools"],
                MemoryScope: MemoryScope.IsolatedBotScope));

        var betaManifest = new AppManifest(
            Title: "Beta Specialist",
            Description: "Beta bot",
            Operations: [],
            Settings: [],
            Slots: [],
            Agent: new AppAgent(
                Instructions: "BETA_BOT",
                Handle: "@beta",
                AllowedContracts: [],
                ToolSources: [$"{OwnerBrain.Value}/connection/mcp/beta-tools"],
                MemoryScope: MemoryScope.IsolatedBotScope));

        var alphaDefWithTools = AgentAppPolicy.Definition(alphaManifest, null, appId: "alpha", brainId: OwnerBrain.Value);
        var betaDefWithTools = AgentAppPolicy.Definition(betaManifest, null, appId: "beta", brainId: OwnerBrain.Value);

        Assert.Equal("memory/bots/alpha", alphaDefWithTools.MemoryScopeKey);
        Assert.Equal("memory/bots/beta", betaDefWithTools.MemoryScopeKey);
        Assert.Equal(["owner/connection/mcp/alpha-tools"], alphaDefWithTools.ToolSources!);
        Assert.Equal(["owner/connection/mcp/beta-tools"], betaDefWithTools.ToolSources!);

        // Cross-brain ToolSources must be rejected fail-closed by AgentAppPolicy.
        var foreignManifest = alphaManifest with
        {
            Agent = alphaManifest.Agent! with { ToolSources = ["other-brain/connection/mcp/stolen"] },
        };
        Assert.Throws<UnauthorizedAccessException>(() =>
            AgentAppPolicy.Definition(foreignManifest, null, appId: "alpha", brainId: OwnerBrain.Value));

        // Seat both specialists (without live MCP/contract surface in local TestOS) and verify IsolatedBotScope memory isolation.
        Assert.Contains(typeof(IntoChat.OS.Modules.Apps.Tasks.ITasks).FullName!, alphaDefWithTools.Contracts);
        var alphaDef = alphaDefWithTools with { ToolSources = null, Contracts = [] };
        var betaDef = betaDefWithTools with { ToolSources = null, Contracts = [] };

        var convId = new NeuronId(OwnerBrain, IConversation.Type, "conversations/squad-isolation");
        using (os.Edge.Enter(os.Edge.Person("owner", "owner", OwnerBrain)))
        {
            var conv = os.Get<IConversation>(convId);
            await conv.Configure(ThreadTrustPosture.OwnerTrusted, ThreadKind.Squad, "Isolated Squad", leadAgentId: "agent-alpha");
            await conv.Seat("agent-alpha", alphaDef);
            await conv.Seat("agent-beta", betaDef);

            await conv.Say("@alpha record ALPHA_SECRET_TOKEN_111");
            await os.Idle();
            await conv.Say("@beta record BETA_SECRET_TOKEN_222");
            await os.Idle();

            // Verify each bot's isolated memory neuron received ONLY its own episode (zero cross-bot leakage).
            var alphaMem = os.Get<IMemory>(new NeuronId(OwnerBrain, "ai.memory", BotMemoryScope.RootFor("alpha")));
            var betaMem = os.Get<IMemory>(new NeuronId(OwnerBrain, "ai.memory", BotMemoryScope.RootFor("beta")));
            var hostMem = os.Get<IMemory>(new NeuronId(OwnerBrain, "ai.memory", "memory"));

            var alphaSnap = await alphaMem.Read();
            var betaSnap = await betaMem.Read();
            var hostSnap = await hostMem.Read();

            Assert.Contains(alphaSnap.Episodes, e => e.Summary.Contains("ALPHA_SECRET_TOKEN_111", StringComparison.Ordinal));
            Assert.DoesNotContain(alphaSnap.Episodes, e => e.Summary.Contains("BETA_SECRET_TOKEN_222", StringComparison.Ordinal));

            Assert.Contains(betaSnap.Episodes, e => e.Summary.Contains("BETA_SECRET_TOKEN_222", StringComparison.Ordinal));
            Assert.DoesNotContain(betaSnap.Episodes, e => e.Summary.Contains("ALPHA_SECRET_TOKEN_111", StringComparison.Ordinal));

            Assert.Empty(hostSnap.Episodes);
        }
    }
}
