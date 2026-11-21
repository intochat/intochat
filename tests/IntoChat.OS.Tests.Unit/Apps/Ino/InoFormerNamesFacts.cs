using DigitalBrain;
using DigitalBrain.Runtime.Http.Surface;
using IntoChat.OS.Apps.Ino;
using IntoChat.OS.Kernel.Composition.Kernel;
using IntoChat.OS.Modules.AI.Agents;
using IntoChat.OS.Modules.AI.Inference;
using IntoChat.OS.Modules.Apps;

namespace IntoChat.OS.Tests.Unit.Apps.Ino;

public sealed class InoFormerNamesFacts
{
    [Fact]
    public void AFormerFullNameFindsTheRenamedContract() =>
        Assert.Equal(typeof(IFiles), LoadedContracts.Find("IntoChat.OS.Modules.Apps.IInoFiles", ["IntoChat.OS"]));

    [Fact]
    public void AFormerWireAliasFindsTheRenamedContract() =>
        Assert.Equal(typeof(IFiles), KernelInvocationSink.ContractOf("ino.files"));

    [Fact]
    public async Task InoReconfiguresWhenOnlyItsContractsChanged()
    {
        var settings = new UserSettingsState() with { DisplayName = "Ada", DefaultModel = "qwen3.5:4b" };
        var currentDef = InoAgentDefinition.For(settings);
        var staleDef = currentDef with
        {
            Contracts = [.. currentDef.Contracts.Select(static c => c == typeof(IFiles).FullName ? "IntoChat.OS.Modules.Apps.IInoFiles" : c)],
        };

        var fake = new RecordingAgent(new AgentState(3, staleDef, null, null, []));

        var host = new InoSetupHost(
            GetState: static () => new InoAppState(),
            ShowState: static _ => Task.CompletedTask,
            GetBrain: static () => new BrainId("owner"),
            GetCatalog: static () => null!,
            GetSettings: static () => null!,
            GetAgent: _ => fake,
            GetChat: static _ => null!,
            GetToneObserver: static _ => null!,
            GetSpeechRecognizer: static () => null!,
            GetGrants: static () => null!,
            GetApprovals: static () => null!,
            GetConversations: static () => null!,
            GetProfileWriter: static () => null!,
            GetAgentContext: static _ => null!,
            GetUiChatThread: static _ => null!,
            FollowAgentBlocked: static _ => Task.CompletedTask,
            FollowChatSignals: static _ => Task.CompletedTask,
            FollowUiChatSignals: static _ => Task.CompletedTask,
            TryOptional: static (_, action) => action(),
            ReadBoolOptional: static (_, fallback, _) => Task.FromResult(fallback),
            ReadApprovalsOptional: static (_, fallback, _) => Task.FromResult(fallback),
            ReadStringArrayOptional: static (_, fallback, _) => Task.FromResult(fallback),
            ReadSettingsOptional: static (_, fallback, _) => Task.FromResult(fallback),
            ReadLocalOptional: static (_, fallback, _) => Task.FromResult(fallback),
            ReadCatalogOptional: static (_, fallback, _) => Task.FromResult(fallback),
            ReadChatOptional: static (_, fallback, _) => Task.FromResult(fallback),
            LogOptional: static (_, _) => { },
            Logger: null);

        await InoModelSetup.EnsureConfiguredAsync(host, fake, settings, settings.DefaultModel, "main");

        Assert.NotNull(fake.ConfiguredDefinition);
        Assert.Equal(3, fake.ConfiguredExpectedRevision);
        Assert.Equal(currentDef.Contracts, fake.ConfiguredDefinition!.Contracts);
    }

    private sealed class RecordingAgent(AgentState initial) : IAgent
    {
        public AgentDefinition? ConfiguredDefinition { get; private set; }
        public long? ConfiguredExpectedRevision { get; private set; }

        public Task Configure(AgentDefinition definition, long expectedRevision)
        {
            ConfiguredDefinition = definition;
            ConfiguredExpectedRevision = expectedRevision;
            return Task.CompletedTask;
        }

        public IAsyncEnumerable<AgentTurnEvent> Run(AgentTurn turn, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<string> Start(AgentTurn turn, string? flowId = null) => throw new NotSupportedException();
        public Task Cancel() => throw new NotSupportedException();
        public Task CancelRun(string runId) => throw new NotSupportedException();
        public Task<AgentState> Read() => Task.FromResult(initial);
        public Task<AiMessage[]> Transcript() => throw new NotSupportedException();
        public Task Clear() => throw new NotSupportedException();
        public Task Post(string text, string id) => throw new NotSupportedException();
    }
}
