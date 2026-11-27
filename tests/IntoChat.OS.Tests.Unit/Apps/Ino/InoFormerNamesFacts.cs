using DigitalBrain;
using DigitalBrain.Runtime.Http.Surface;
using IntoChat.OS.Apps.Ino;
using IntoChat.OS.Host.Modules;
using IntoChat.OS.Host.Primitives;
using IntoChat.OS.Kernel;
using IntoChat.OS.Kernel.Composition.Kernel;
using IntoChat.OS.Modules.AI.Agents;
using IntoChat.OS.Modules.AI.Models;
using IntoChat.OS.Modules.Apps;
using IntoChat.OS.Modules.Flutter.Conversations;
using IntoChat.OS.Modules.Identity.Contracts;

namespace IntoChat.OS.Tests.Unit.Apps.Ino;

public sealed class InoFormerNamesFacts
{
    private static readonly IContractAssemblySource Assemblies = new ModuleInventory(OperatingSystemModules.Modules);

    [Fact]
    public void AFormerFullNameFindsTheRenamedContract() =>
        Assert.Equal(typeof(IFiles), LoadedContracts.Find("IntoChat.OS.Modules.Apps.IInoFiles", Assemblies));

    [Fact]
    public void AFormerWireAliasFindsTheRenamedContract() =>
        Assert.Equal(typeof(IFiles), KernelInvocationSink.ContractOf("ino.files"));

    [Fact]
    public async Task InoSeatsItsUpdatedContractsOnTheConversation()
    {
        var settings = new UserSettingsState() with { DisplayName = "Ada", DefaultModel = "qwen3.5:4b" };
        var currentDef = InoAgentDefinition.For(settings);
        var fake = new RecordingConversation();

        var host = new InoSetupHost(
            GetState: static () => new InoAppState(),
            ShowState: static _ => Task.CompletedTask,
            GetBrain: static () => new BrainId("owner"),
            GetCatalog: static () => null!,
            GetDevice: static () => null,
            GetSettings: static () => null!,
            GetConversation: _ => fake,
            GetSessions: static () => null!,
            GetGrants: static () => null!,
            GetApprovals: static () => null!,
            GetProfileWriter: static () => null!,
            FollowConversation: static _ => Task.CompletedTask,
            RefreshConversation: static _ => Task.CompletedTask,
            TryOptional: static (_, action) => action(),
            ReadBoolOptional: static (_, fallback, _) => Task.FromResult(fallback),
            ReadApprovalsOptional: static (_, fallback, _) => Task.FromResult(fallback),
            ReadStringArrayOptional: static (_, fallback, _) => Task.FromResult(fallback),
            ReadSettingsOptional: static (_, fallback, _) => Task.FromResult(fallback),
            ReadDeviceOptional: static (_, fallback, _) => Task.FromResult(fallback),
            ReadCatalogOptional: static (_, fallback, _) => Task.FromResult(fallback),
            ReadSessionPageOptional: static (_, fallback, _) => Task.FromResult(fallback),
            LogOptional: static (_, _) => { },
            Logger: null);

        await InoModelSetup.EnsureSeatedAsync(host, "main", settings, settings.DefaultModel);

        Assert.Equal("ino", fake.SeatedAgentId);
        Assert.NotNull(fake.SeatedDefinition);
        Assert.Equal(currentDef.Contracts, fake.SeatedDefinition!.Contracts);
    }

    private sealed class RecordingConversation : IConversation
    {
        public string? SeatedAgentId { get; private set; }
        public AgentDefinition? SeatedDefinition { get; private set; }

        public Task Seat(string agentId, AgentDefinition definition)
        {
            SeatedAgentId = agentId;
            SeatedDefinition = definition;
            return Task.CompletedTask;
        }

        public Task Configure(ThreadTrustPosture posture, ThreadKind kind, string? title = null, string? leadAgentId = null) => Task.CompletedTask;
        public Task Say(string text, ModelId? model = null, AttachmentRef[]? attachments = null, string? messageId = null, EntryId? replyTo = null) => throw new NotSupportedException();
        public Task Stop() => Task.CompletedTask;
        public Task Regenerate(EntryId reply) => throw new NotSupportedException();
        public Task Post(string text, string id, Visibility visibility = Visibility.Everyone) => Task.CompletedTask;
        public Task Voiced(string heard, string reply, ModelId model, string id) => Task.CompletedTask;
        public Task<ConversationView> Read(long? sinceRevision = null) => Task.FromResult(new ConversationView(ConversationId.Parse("main"), 0, [], [], null, [], false, []));
        public Task Invite(AccountId friend, DigitalBrain.BrainId? friendBrain = null, DigitalBrain.Components.Role role = DigitalBrain.Components.Role.Editor) => throw new NotSupportedException();
        public Task Pin(string screenType, string screenKey = "default") => Task.CompletedTask;
        public Task<DigitalBrain.Units.PublishedView> Act(string screenType, string screenKey, DigitalBrain.UI.Command command) => throw new NotSupportedException();
        public Task Here() => Task.CompletedTask;
        public Task Leave() => Task.CompletedTask;
        public Task React(EntryId target, string emoji, bool remove = false) => Task.CompletedTask;
        public Task Edit(EntryId target, string text) => Task.CompletedTask;
        public Task Delete(EntryId target) => Task.CompletedTask;
    }
}
