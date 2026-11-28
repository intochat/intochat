using DigitalBrain;
using DigitalBrain.Access;
using DigitalBrain.Capabilities;
using DigitalBrain.Capabilities.Connections.Broker;
using DigitalBrain.Confirmation;
using DigitalBrain.Runtime.Orleans;
using DigitalBrain.Runtime.Orleans.Capabilities;
using DigitalBrain.Testing;
using DigitalBrain.Units;
using IntoChat.OS.Apps;
using IntoChat.OS.Modules.Apps;
using IntoChat.OS.Modules.Apps.Approvals;
using IntoChat.OS.Modules.Apps.Social;
using IntoChat.OS.Modules.Flutter;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;
using InstallRequest = DigitalBrain.Units.InstallRequest;
using InstallStatus = DigitalBrain.Units.InstallStatus;

namespace IntoChat.OS.Modules.Apps.Tests.Integration.Social;

/// <summary>
/// Verifies the full social App-Post lifecycle and 1-click fork/install consent flow:
/// 1. Author publishes an App-Post containing micro-app unit files and needs manifest to the post store.
/// 2. Forking the post (the Friends screen does this; FriendsOneFeedFacts drives the screen) takes the files the stored post carries.
/// 3. The unit files are deployed to Synapse Studio and registered in IInstalls.
/// 4. Because the unit declares needs (views/reactions), IInstalls marks it Pending and raises a SynapseStaged approval card.
/// 5. Recipient executes ApproveAsPerson to grant consent.
/// 6. IInstalls transitions the unit status to Installed.
/// </summary>
public sealed class FriendsForkConsentFacts
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private const string ShippedUnitAlias = "circleroom";

    private sealed class EmptyCapabilityConnections : ICapabilityConnections
    {
        public Task<IReadOnlyList<BrainConnection>> ListInOrder(BrainId brain, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<BrainConnection>>([]);

        public Task<TCapability> Bind<TCapability>(BrainId brain, string connectionKey, string callerApp, CancellationToken ct = default)
            where TCapability : class
            => throw new NotSupportedException();

        public Task RequestConnection(BrainId brain, CapabilityId capability, string callerApp, CancellationToken ct = default)
            => Task.CompletedTask;
    }

    private sealed class SocialConsentFixture : BothRuntimesFixture
    {
        protected override IReadOnlyList<Type> Modules => [.. CatalogFamily.Modules, typeof(FlutterModule)];

        protected override void ConfigureSilo(IServiceCollection services)
        {
            AppsModule.ConfigureServices(services, new ConfigurationBuilder().Build());
            IntoChat.OS.Modules.Authoring.AuthoringModule.ConfigureServices(services, new ConfigurationBuilder().Build());
            services.AddJournalPayloads(typeof(IApp).Assembly);
            services.AddJournalPayloads(typeof(AppsModule).Assembly);
            services.AddJournalPayloads(typeof(IntoChat.OS.Modules.AI.Agents.Context.Signals.ContextUpdated).Assembly);
            services.AddJournalPayloads(typeof(FlutterModule).Assembly);
            services.AddJournalPayloads(typeof(IntoChat.OS.Modules.Flutter.Views.IView).Assembly);
            services.AddJournalPayloads(typeof(DigitalBrain.Signals.Activated).Assembly);
            services.TryAddSingleton<ICapabilityConnections, EmptyCapabilityConnections>();
            services.TryAddSingleton(TimeProvider.System);
        }
    }

    private static IDisposable AsPerson(INeuronRuntime runtime)
        => runtime.Edge.Enter(runtime.Edge.Person("person", "person", runtime.Brain));

    private static IPosts PostsOf(INeuronRuntime runtime) => runtime.Get<IPosts>(new NeuronId(runtime.Brain, "social.posts", IPosts.DefaultKey));

    // An App-Post as the Friends screen publishes it: the unit's files travel in the post.
    private static PostDraft AppPost(string id, string text, string title, IReadOnlyDictionary<string, string> unitFiles) => new(
        text,
        [],
        new AppDefinitionShare(ShippedUnitAlias, title, [.. unitFiles.OrderBy(static file => file.Key, StringComparer.Ordinal).Select(static file => new SharedSourceFile(file.Key, file.Value))]),
        Id: id,
        Title: title,
        Audience: new Audience.Friends());

    // What forking a post does with its files: the ones the stored post carries go to Synapse Studio.
    private static async Task ForkIntoStudio(INeuronRuntime runtime, string postId)
    {
        var post = Assert.Single((await PostsOf(runtime).Since(0, Ct)).Posts, stored => stored.Id == postId);
        var files = post.SharedApp!.Files.ToDictionary(static file => file.Path, static file => file.Content, StringComparer.Ordinal);
        await runtime.Get<ISynapseStudio>(ISynapseStudio.Key(runtime.Brain.Value)).DeployFiles(post.SharedApp.AppId, files, new Dictionary<string, string>(StringComparer.Ordinal));
    }

    [Fact]
    public async Task AuthorPublishesAppPostWithUnitFiles_PostIsPersistedAndVisibleInFeed()
    {
        await using var fixture = new SocialConsentFixture();
        await using var runtime = await fixture.Start(NeuronRuntimeKind.Silo, Ct);
        using var person = AsPerson(runtime);

        var unitFiles = ShippedUnits.Files(ShippedUnitAlias);
        Assert.NotEmpty(unitFiles);

        await PostsOf(runtime).Publish(AppPost("post-room-1", "Check out this circle room app template!", "Shared Circle Room", unitFiles), Ct);

        var found = Assert.Single((await PostsOf(runtime).Since(0, Ct)).Posts);
        Assert.Equal("post-room-1", found.Id);
        Assert.Equal(ShippedUnitAlias, found.SharedApp!.AppId);
        var paths = found.SharedApp.Files.Select(static file => file.Path).ToArray();
        Assert.Contains("CircleRoom.cs", paths);
        Assert.Contains("spec.md", paths);
        Assert.Contains("scenarios.json", paths);
    }

    [Fact]
    public async Task RecipientForksAppPost_StagesUnitIntoSynapseStudio()
    {
        await using var fixture = new SocialConsentFixture();
        await using var runtime = await fixture.Start(NeuronRuntimeKind.Silo, Ct);
        using var person = AsPerson(runtime);

        var studio = runtime.Get<ISynapseStudio>(ISynapseStudio.Key(runtime.Brain.Value));
        var unitFiles = ShippedUnits.Files(ShippedUnitAlias);

        await PostsOf(runtime).Publish(AppPost("post-source", "Original shared template", "Original Template", unitFiles), Ct);

        // Fork the post: the unit files the stored post carries are deployed to ISynapseStudio
        await ForkIntoStudio(runtime, "post-source");

        var studioSources = await studio.SourceFiles(ShippedUnitAlias);
        Assert.NotEmpty(studioSources);
        Assert.True(studioSources.ContainsKey("CircleRoom.cs"));
    }

    [Fact]
    public async Task ForkedAppPostInstall_AwaitsSynapseStagedConsentAndApprovesToInstalled()
    {
        await using var fixture = new SocialConsentFixture();
        await using var runtime = await fixture.Start(NeuronRuntimeKind.Silo, Ct);
        using var person = AsPerson(runtime);

        var installs = runtime.Get<IInstalls>(new NeuronId(runtime.Brain, "synapse.installs", "default"));
        var approvals = runtime.Get<IApprovals>(IApprovals.Key(runtime.Brain.Value));
        var unitFiles = ShippedUnits.Files(ShippedUnitAlias);

        // 1. Author publishes App-Post
        await PostsOf(runtime).Publish(AppPost("post-room-consent", "Circle room template requiring consent", "Circle Room Unit", unitFiles), Ct);

        // 2. Recipient forks the post
        await ForkIntoStudio(runtime, "post-room-consent");

        // 3. 1-Click Install trigger on IInstalls
        var defaultBindings = new Dictionary<string, string>(StringComparer.Ordinal);
        var outcome = await installs.Install(new DigitalBrain.Units.InstallRequest(ShippedUnitAlias, defaultBindings), Ct);

        // 4. Verification: Needs require person consent; install must be Pending
        Assert.Equal(DigitalBrain.Units.InstallStatus.Pending, outcome.Status);
        Assert.Null(await installs.Describe(ShippedUnitAlias, Ct));

        var pendingInstalls = await installs.Pending(Ct);
        var pendingAsk = Assert.Single(pendingInstalls, p => p.Alias == ShippedUnitAlias);
        Assert.False(string.IsNullOrWhiteSpace(pendingAsk.PlanId));

        // 5. Verification: SynapseStaged approval card exists in IApprovals
        var pendingApprovals = await approvals.Pending(Ct);
        var card = Assert.Single(pendingApprovals, a => a.PlanId == pendingAsk.PlanId);
        Assert.Equal(ApprovalKind.SynapseStaged, card.Kind);
        Assert.Equal(ShippedUnitAlias, card.Subject);
        Assert.Contains("show a view", card.Detail, StringComparison.OrdinalIgnoreCase);

        // 6. Action: Person approves the pending plan
        await runtime.ApproveAsPerson(pendingAsk.PlanId, Ct);

        // 7. Verification: Status transitions to Installed and Pending clears
        for (var i = 0; i < 100 && await installs.Describe(ShippedUnitAlias, Ct) is null; i++)
        {
            await Task.Delay(50, Ct);
        }

        var installed = await installs.Describe(ShippedUnitAlias, Ct);
        Assert.NotNull(installed);
        Assert.Equal(ShippedUnitAlias, installed.Alias);
        Assert.Empty(await installs.Pending(Ct));
    }
}
