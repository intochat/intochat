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
using IntoChat.OS.Apps.Friends;
using IntoChat.OS.Modules.Apps;
using IntoChat.OS.Modules.Apps.Approvals;
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
/// 1. Author publishes an App-Post containing micro-app unit files and needs manifest.
/// 2. Recipient views the post in their Friends feed and invokes ForkPost / fork_app.
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
        protected override IReadOnlyList<Type> Modules => [typeof(AppsModule), typeof(FlutterModule)];

        protected override void ConfigureSilo(IServiceCollection services)
        {
            AppsModule.ConfigureServices(services, new ConfigurationBuilder().Build());
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

    [Fact]
    public async Task AuthorPublishesAppPostWithUnitFiles_PostIsPersistedAndVisibleInFeed()
    {
        await using var fixture = new SocialConsentFixture();
        await using var runtime = await fixture.Start(NeuronRuntimeKind.Silo, Ct);
        using var person = AsPerson(runtime);

        var profile = runtime.Get<IProfile>(new NeuronId(runtime.Brain, "os.profile", "profile"));
        var unitFiles = ShippedUnits.Files(ShippedUnitAlias);
        Assert.NotEmpty(unitFiles);

        var post = new ProfilePostItem(
            Id: "post-room-1",
            Content: "Check out this circle room app template!",
            CreatedAt: DateTimeOffset.UtcNow,
            Circle: "friends",
            Title: "Shared Circle Room",
            TemplateAppId: ShippedUnitAlias,
            TemplateSource: unitFiles.TryGetValue("CircleRoom.cs", out var src) ? src : "",
            EmbeddedApps: [new("room-view", ShippedUnitAlias, "Circle Room", $"{runtime.Brain.Value}/apps/{ShippedUnitAlias}")],
            UnitFiles: unitFiles,
            AuthorBrainId: runtime.Brain.Value);

        var updated = await profile.PublishAppPost(post);

        Assert.NotEmpty(updated.Posts);
        var found = Assert.Single(updated.Posts, p => p.Id == "post-room-1");
        Assert.Equal("post-room-1", found.Id);
        Assert.Equal(ShippedUnitAlias, found.TemplateAppId);
        Assert.NotNull(found.UnitFiles);
        Assert.True(found.UnitFiles.ContainsKey("CircleRoom.cs"));
        Assert.True(found.UnitFiles.ContainsKey("spec.md"));
        Assert.True(found.UnitFiles.ContainsKey("scenarios.json"));
    }

    [Fact]
    public async Task RecipientForksAppPost_StagesUnitIntoSynapseStudio()
    {
        await using var fixture = new SocialConsentFixture();
        await using var runtime = await fixture.Start(NeuronRuntimeKind.Silo, Ct);
        using var person = AsPerson(runtime);

        var profile = runtime.Get<IProfile>(new NeuronId(runtime.Brain, "os.profile", "profile"));
        var studio = runtime.Get<ISynapseStudio>(ISynapseStudio.Key(runtime.Brain.Value));
        var unitFiles = ShippedUnits.Files(ShippedUnitAlias);

        var post = new ProfilePostItem(
            Id: "post-source",
            Content: "Original shared template",
            CreatedAt: DateTimeOffset.UtcNow,
            Circle: "public",
            Title: "Original Template",
            TemplateAppId: ShippedUnitAlias,
            TemplateSource: unitFiles["CircleRoom.cs"],
            UnitFiles: unitFiles,
            AuthorBrainId: runtime.Brain.Value);

        await profile.PublishAppPost(post);

        // Fork the post into a new remixed post
        var forkedProfile = await profile.ForkPost("post-source", "post-fork-1", "My Remixed Room");

        var forkedPost = Assert.Single(forkedProfile.Posts, p => p.Id == "post-fork-1");
        Assert.Equal("post-fork-1", forkedPost.Id);

        // The fork automatically deploys unit files to ISynapseStudio
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

        var profile = runtime.Get<IProfile>(new NeuronId(runtime.Brain, "os.profile", "profile"));
        var studio = runtime.Get<ISynapseStudio>(ISynapseStudio.Key(runtime.Brain.Value));
        var installs = runtime.Get<IInstalls>(new NeuronId(runtime.Brain, "synapse.installs", "default"));
        var approvals = runtime.Get<IApprovals>(IApprovals.Key(runtime.Brain.Value));
        var unitFiles = ShippedUnits.Files(ShippedUnitAlias);

        // 1. Author publishes App-Post
        var post = new ProfilePostItem(
            Id: "post-room-consent",
            Content: "Circle room template requiring consent",
            CreatedAt: DateTimeOffset.UtcNow,
            Circle: "friends",
            Title: "Circle Room Unit",
            TemplateAppId: ShippedUnitAlias,
            TemplateSource: unitFiles["CircleRoom.cs"],
            UnitFiles: unitFiles,
            AuthorBrainId: runtime.Brain.Value);

        await profile.PublishAppPost(post);

        // 2. Recipient forks the post
        await profile.ForkPost("post-room-consent", "post-room-forked", "Forked Circle Room");

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
