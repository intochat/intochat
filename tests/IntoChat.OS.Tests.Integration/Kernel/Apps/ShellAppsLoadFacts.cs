using IntoChat.OS.Tests;
using DigitalBrain.Access;
using DigitalBrain;
using DigitalBrain.Capabilities;
using DigitalBrain.Capabilities.Connections.Broker;
using DigitalBrain.Capabilities.Identity;
using DigitalBrain.Contracts;
using DigitalBrain.Hosting;
using DigitalBrain.Modules;
using DigitalBrain.Runtime.Orleans;
using DigitalBrain.Runtime.Orleans.Capabilities;
using DigitalBrain.Runtime.Orleans.Hosting;
using DigitalBrain.Runtime.Orleans.Testing;
using DigitalBrain.Signals;
using DigitalBrain.Testing;
using DigitalBrain.UI;
using IntoChat.OS.Apps.Settings;
using IntoChat.OS.Authority;
using IntoChat.OS.Modules.Apps;
using IntoChat.OS.Modules.Flutter;
using IntoChat.OS.Modules.Flutter.Views;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using IntoChat.OS.Modules.Identity;

namespace IntoChat.OS.Kernel.Tests.Integration.Apps;

public sealed class ShellAppsLoadFacts : IAsyncLifetime
{
    private IInProcessHost? _host;

    private IServiceProvider Services => (_host ?? throw new InvalidOperationException("Silo host is not started.")).Services;

    public async ValueTask InitializeAsync()
    {
        var signalAssemblies = new[]
        {
            typeof(Activated).Assembly,
            typeof(AppsModule).Assembly,
            typeof(IApp).Assembly,
            typeof(IdentityModule).Assembly,
            typeof(AuthorityPlainNeurons).Assembly,
            typeof(FlutterModule).Assembly,
            typeof(IView).Assembly,
        }.Distinct().ToArray();

        var factory = new LocalSiloHostFactory(
            new BrainStorageRegistration(signalAssemblies),
            services =>
            {
                AppsModule.ConfigureServices(services, new ConfigurationBuilder().Build());
                IntoChat.OS.Modules.Authoring.AuthoringModule.ConfigureServices(services, new ConfigurationBuilder().Build());
                services.AddOperatingSystemBoot();
                services.TryAddSingleton<ICapabilityConnections, EmptyCapabilityConnections>();
                services.AddIdentityCapability<TestIdentityDirectoryPort>();
                services.AddSingleton(sp => new FeatureCatalog(sp.GetServices<CompiledAppsRegistration>().Select(r => r.Assembly)));
            });

        _host = await factory.Start(
            new InProcessHostOptions
            {
                Modules =
                [
                    typeof(AppsModule),
                    typeof(IntoChat.OS.Modules.Runs.RunsModule),
                    typeof(IntoChat.OS.Modules.Authoring.AuthoringModule),
                    typeof(IdentityModule),
                    typeof(AuthorityPlainNeurons),
                    typeof(FlutterModule),
                    typeof(FlameStubModule),
                ],
            },
            CancellationToken.None);
    }

    public async ValueTask DisposeAsync()
    {
        if (_host is not null)
        {
            await _host.DisposeAsync();
        }
    }

    [Fact]
    public async Task ShellAppsLoadFromTheirAssemblies()
    {
        var catalog = Services.GetRequiredService<FeatureCatalog>();

        var listings = await catalog.Listings();
        Assert.Contains(listings, listing => listing.Publisher == "os" && listing.Name == "settings");

        var origin = Assert.IsType<FeatureOrigin.ModuleSource>(await catalog.Origin("os", "settings"));
        Assert.Same(typeof(SettingsApp).Assembly, origin.Assembly);

        const string brain = "nofeaturesbrain";
        var edge = Services.GetRequiredService<EdgeAuthority>();
        using var _ = edge.Enter(edge.Person(brain, brain, new BrainId(brain)));

        var loader = Services.GetRequiredService<FeatureLoader>();
        var settingsBundle = await catalog.Find("os", "settings");
        Assert.NotNull(settingsBundle);
        var snapshot = await loader.Load(
            brain,
            "settings",
            settingsBundle,
            new AppOrigin(ShippedApps.Publisher, new AppSource.Shipped(), "settings", settingsBundle.Version),
            TestContext.Current.CancellationToken);
        Assert.Equal("Settings", snapshot.Manifest.Title);
        Assert.Equal(AppStatus.Live, snapshot.Status);
    }

    [Fact]
    public async Task UngrantedAppCannotReachInstalledModules()
    {
        // 1. TestOS tier
        await using (var os = TestOS.Local())
        {
            os.RegisterCatalog();
            using var ungrantedCaller = os.Edge.Enter(
                os.Edge.App("local/apps/calculator", new Actor.Person("local", "local"), new BrainId("local")));

            var localModules = os.Get<IInstalledModules>(new NeuronId(new BrainId("local"), "apps.installed-modules", "modules"));
            await Assert.ThrowsAsync<UnauthorizedAccessException>(localModules.List);
        }

        // 2. Silo tier
        const string brain = "grantbrain";
        var edge = Services.GetRequiredService<EdgeAuthority>();
        using (edge.Enter(edge.App($"{brain}/apps/calculator", new Actor.Person(brain, brain), new BrainId(brain))))
        {
            var siloModules = SiloNeurons.Get<IInstalledModules>(Services, $"{brain}/modules");
            await Assert.ThrowsAsync<UnauthorizedAccessException>(siloModules.List);
        }

        // 3. Granted SettingsApp succeeds on the silo
        using (edge.Enter(edge.App($"{brain}/apps/settings", new Actor.Person(brain, brain), new BrainId(brain))))
        {
            var siloModules = SiloNeurons.Get<IInstalledModules>(Services, $"{brain}/modules");
            var rows = await siloModules.List();
            Assert.Contains(rows, r => r.Id == "apps");
        }
    }

    [Fact]
    public async Task SettingsScenariosPassFromTheNewProject()
    {
        await using var os = TestOS.Local();
        using var person = os.Edge.Enter(os.Edge.Person("test", "test", new BrainId("test")));
        os.RegisterCatalog();
        os.Register(typeof(SettingsApp).Assembly);
        var screen = os.Get<IScreen>(new NeuronId(new BrainId("test"), "app.settings", "default"));

        var initialView = await screen.Read();
        Assert.NotNull(initialView);
        Assert.Equal("app.settings", initialView.Alias);
        Assert.True(initialView.Generation >= 1);

        await screen.Request(new SettingsOpen());
        await os.Idle();

        var openView = await screen.Read();
        Assert.NotNull(openView);
        Assert.Equal("app.settings", openView.Alias);
    }

    [DigitalBrain.Modules.Module("flame-stub")]
    private sealed class FlameStubModule : DigitalBrain.Modules.Module
    {
        public static ModuleManifest Manifest { get; } = new([], [], [], [], []);
    }

    private sealed class EmptyCapabilityConnections : ICapabilityConnections
    {
        public Task<IReadOnlyList<BrainConnection>> ListInOrder(BrainId brain, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<BrainConnection>>([]);

        public Task<TCapability> Bind<TCapability>(BrainId brain, string connectionKey, string callerApp, CancellationToken ct = default)
            where TCapability : class
            => throw new NotSupportedException();

        public Task RequestConnection(BrainId brain, CapabilityId capability, string callerApp, CancellationToken ct = default)
            => Task.CompletedTask;
    }

    private sealed class TestIdentityDirectoryPort : IIdentityDirectoryPort
    {
        public Task<Account?> ReadAccount(string accountId, CancellationToken cancellationToken)
            => Task.FromResult<Account?>(new Account { AccountId = accountId, Name = accountId, OwnerPrincipalId = accountId, CreatedAt = DateTimeOffset.UtcNow });

        public Task<Account?> OwnerOfBrain(string brainId, CancellationToken cancellationToken)
            => Task.FromResult<Account?>(new Account { AccountId = brainId, Name = brainId, OwnerPrincipalId = brainId, CreatedAt = DateTimeOffset.UtcNow });

        public Task EnsureAccount(Account account, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<BrainProvision> CreateBrain(string accountId, string principalId, string operationId, string displayName, BrainKind kind, CancellationToken cancellationToken)
            => Task.FromResult(new BrainProvision(new Member { PrincipalId = principalId, AccountId = accountId, BrainId = accountId, Role = MemberRole.Owner, DisplayName = displayName, JoinedAt = DateTimeOffset.UtcNow }, true));

        public Task<BrainProvision> EnsureBrain(Member owner, CancellationToken cancellationToken)
            => Task.FromResult(new BrainProvision(owner, false));

        public Task<BrainProvision> ProvisionOwner(Member owner, CancellationToken cancellationToken)
            => Task.FromResult(new BrainProvision(owner, true));

        public Task<bool> IsRegistered(string principalId, CancellationToken cancellationToken) => Task.FromResult(true);

        public Task<BrainProvision> RegisterPrincipal(string principalId, string displayName, CancellationToken cancellationToken)
            => Task.FromResult(new BrainProvision(new Member { PrincipalId = principalId, AccountId = principalId, BrainId = principalId, Role = MemberRole.Owner, DisplayName = displayName, JoinedAt = DateTimeOffset.UtcNow }, false));

        public Task<BrainProvision?> CompleteRegistration(string principalId, CancellationToken cancellationToken)
            => Task.FromResult<BrainProvision?>(null);

        public Task<IReadOnlyList<DigitalBrain.Capabilities.Identity.BrainEntry>> BrainsOf(string principalId, CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<DigitalBrain.Capabilities.Identity.BrainEntry>>([new DigitalBrain.Capabilities.Identity.BrainEntry(principalId, BrainKind.Personal)]);

        public Task<BrainKind> KindOf(string brainScopeId, CancellationToken cancellationToken) => Task.FromResult(BrainKind.Personal);

        public Task<bool> BrainExists(string brainScopeId, CancellationToken cancellationToken) => Task.FromResult(true);

        public Task<Member?> Membership(string brainScopeId, string principalId, CancellationToken cancellationToken)
            => Task.FromResult<Member?>(new Member { PrincipalId = principalId, AccountId = principalId, BrainId = brainScopeId, Role = MemberRole.Owner, DisplayName = principalId, JoinedAt = DateTimeOffset.UtcNow });

        public Task<Member?> EnsureMember(Member member, CancellationToken cancellationToken)
            => Task.FromResult<Member?>(member);

        public Task RemoveMember(string brainScopeId, string accountId, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task RemovePrincipalMember(string brainScopeId, string principalId, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<DigitalBrain.Capabilities.Identity.CallDecision?> Authorize(DigitalBrain.Capabilities.Identity.CallRequest request, CancellationToken cancellationToken)
            => Task.FromResult<DigitalBrain.Capabilities.Identity.CallDecision?>(new DigitalBrain.Capabilities.Identity.CallDecision { Allowed = true });

        public Task<IReadOnlyList<Grant>> ListGrants(string brainScopeId, CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<Grant>>([]);

        public Task<Grant?> GrantAsOwner(Grant grant, CancellationToken cancellationToken)
            => Task.FromResult<Grant?>(grant);

        public Task<bool> RevokeAsOwner(string appId, string semanticTypeId, GrantMode mode, CancellationToken cancellationToken)
            => Task.FromResult(true);
    }
}
