using DigitalBrain.Capabilities.Connections.Broker;
using DigitalBrain.Signals;
using DigitalBrain;
using DigitalBrain.Capabilities;
using DigitalBrain.Capabilities.Identity;
using DigitalBrain.Contracts;
using DigitalBrain.Runtime.Orleans;
using DigitalBrain.Runtime.Orleans.Capabilities;
using DigitalBrain.Runtime.Orleans.Hosting;
using DigitalBrain.Runtime.Orleans.Testing;
using DigitalBrain.Spi;
using DigitalBrain.Testing;
using DigitalBrain.UI;
using IntoChat.OS.Apps.Settings;
using IntoChat.OS.Authority;
using IntoChat.OS.Authority.Identity;
using IntoChat.OS.Framework;
using IntoChat.OS.Modules.Apps;
using IntoChat.OS.Modules.Flutter;
using IntoChat.OS.Modules.Flutter.Views;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace IntoChat.OS.Kernel.Tests.Integration.Apps;

public sealed class ShellAppsLoadFacts : IAsyncLifetime
{
    private readonly string _sideloadRoot = Directory.CreateTempSubdirectory("shell-apps-load-").FullName;
    private IInProcessHost? _host;

    private IServiceProvider Services => (_host ?? throw new InvalidOperationException("Silo host is not started.")).Services;

    public async ValueTask InitializeAsync()
    {
        var folder = Directory.CreateDirectory(Path.Combine(_sideloadRoot, "notes"));
        await File.WriteAllTextAsync(
            Path.Combine(folder.FullName, "app.cs"),
            AppDeclaration.Write(new("notes", "1.0.0", new AppManifest("Notes", "Notes.", [], [], []), "")));
        await File.WriteAllTextAsync(Path.Combine(folder.FullName, "note.cs"), "static class Note { }\n");

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
                services.AddOperatingSystemBoot();
                services.TryAddSingleton<ICapabilityConnections, EmptyCapabilityConnections>();
                services.AddIdentityCapability<TestIdentityDirectoryPort>();
                services.AddSingleton(sp =>
                {
                    var git = new GitSideload((_, _) => Task.FromResult(0), GitSideload.MaxBytes);
                    var catalog = new FeatureCatalog(
                        directory: null,
                        modules: sp.GetServices<CompiledAppsRegistration>().Select(r => r.Assembly),
                        dev: null,
                        sideload: new SideloadDirectory(_sideloadRoot),
                        git: git);
                    return catalog;
                });
                services.AddSingleton(sp =>
                {
                    var git = new GitSideload((_, _) => Task.FromResult(0), GitSideload.MaxBytes);
                    return new SideloadInstaller(sp.GetRequiredService<FeatureCatalog>(), git, (req, _) => Task.FromResult(IntoChat.OS.Modules.Apps.InstallOutcome.Installed(req.Ref.App)));
                });
            });

        _host = await factory.Start(
            new InProcessHostOptions
            {
                Modules =
                [
                    typeof(AppsModule),
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
        if (Directory.Exists(_sideloadRoot))
        {
            Directory.Delete(_sideloadRoot, recursive: true);
        }
    }

    [Fact]
    public async Task ShellAppsLoadWithoutAFeaturesFolder()
    {
        var catalog = Services.GetRequiredService<FeatureCatalog>();
        Assert.Null(catalog.Directory);

        var listings = await catalog.Listings();
        Assert.Contains(listings, listing => listing.Publisher == "os" && listing.Name == "settings");

        var origin = Assert.IsType<FeatureOrigin.Module>(await catalog.Origin("os", "settings"));
        Assert.Same(typeof(SettingsApp).Assembly, origin.Assembly);

        const string brain = "nofeaturesbrain";
        using var _ = RuntimeCallerContext.Stamp(CapabilityCallers.Of(brain, brain, brain));

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
            os.Register(typeof(AppsModule).Assembly);
            using var ungrantedCaller = RuntimeCallerContext.Stamp(
                CapabilityCallers.Of("app:local/apps/calculator", "local", "local", CallerKind.App, "local/apps/calculator"));

            var localModules = os.Get<IInstalledModules>(new NeuronId(new BrainId("local"), "apps.installed-modules", "modules"));
            await Assert.ThrowsAsync<UnauthorizedAccessException>(localModules.List);

            var localSideload = os.Get<ISideload>(new NeuronId(new BrainId("local"), "apps.sideload", "sideload"));
            await Assert.ThrowsAsync<UnauthorizedAccessException>(localSideload.List);
        }

        // 2. Silo tier
        const string brain = "grantbrain";
        using (RuntimeCallerContext.Stamp(CapabilityCallers.Of(
            $"app:{brain}/apps/calculator",
            brain,
            brain,
            CallerKind.App,
            $"{brain}/apps/calculator")))
        {
            var siloModules = SiloNeurons.Get<IInstalledModules>(Services, $"{brain}/modules");
            await Assert.ThrowsAsync<UnauthorizedAccessException>(siloModules.List);

            var siloSideload = SiloNeurons.Get<ISideload>(Services, $"{brain}/sideload");
            await Assert.ThrowsAsync<UnauthorizedAccessException>(siloSideload.List);
        }

        // 3. Granted SettingsApp succeeds on the silo
        using (RuntimeCallerContext.Stamp(CapabilityCallers.Of(
            $"app:{brain}/apps/settings",
            brain,
            brain,
            CallerKind.App,
            $"{brain}/apps/settings")))
        {
            var siloModules = SiloNeurons.Get<IInstalledModules>(Services, $"{brain}/modules");
            var rows = await siloModules.List();
            Assert.Contains(rows, r => r.Id == "apps");

            var siloSideload = SiloNeurons.Get<ISideload>(Services, $"{brain}/sideload");
            var sideloadRows = await siloSideload.List();
            Assert.Contains(sideloadRows, r => r.Name == "notes");
        }
    }

    [Fact]
    public async Task SettingsScenariosPassFromTheNewProject()
    {
        var report = await BddRunner.VerifyAsync<SettingsApp>(
            fakes: os => os.Register(typeof(AppsModule).Assembly),
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(report.Passed);
        Assert.Single(report.Scenarios);

        const string brain = "settingsnewproj";
        using var _ = RuntimeCallerContext.Stamp(CapabilityCallers.Of(brain, brain, brain));

        var appKey = IApp.Key(brain, "settings");
        var appNeuron = SiloNeurons.Get<IApp>(Services, appKey);
        var bundle = SettingsApp.Bundle with
        {
            Files = new Dictionary<string, string>(StringComparer.Ordinal),
            CompiledType = typeof(SettingsApp).AssemblyQualifiedName,
        };
        await appNeuron.Import(bundle, new AppOrigin(ShippedApps.Publisher, new AppSource.Shipped(), "settings", bundle.Version));

        var host = Services.GetRequiredService<IntoChat.OS.Modules.Apps.Hosting.InProcess.InProcessAppHost>();
        await host.Request(appKey, "open", new SettingsOpen(), TestContext.Current.CancellationToken);
        await host.Request(appKey, "sideload-list", new SettingsSideloadList(), TestContext.Current.CancellationToken);

        var turns = host.TryGetTurns(appKey);
        Assert.NotNull(turns);
        var settingsInstance = Assert.IsType<SettingsApp>(turns.App);
        var listed = Assert.IsType<Collection>(settingsInstance.State.OverrideBody);
        Assert.Contains(listed.Entries, e => e.Id == "notes");
    }

    [DigitalBrain.Module("flame-stub")]
    private sealed class FlameStubModule : DigitalBrain.Module
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

        public Task<Member?> EnsureMember(CapabilityCaller caller, Member member, CancellationToken cancellationToken)
            => Task.FromResult<Member?>(member);

        public Task RemoveMember(CapabilityCaller caller, string brainScopeId, string accountId, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task RemovePrincipalMember(CapabilityCaller caller, string brainScopeId, string principalId, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<DigitalBrain.Capabilities.Identity.CallDecision?> Authorize(CapabilityCaller caller, DigitalBrain.Capabilities.Identity.CallRequest request, CancellationToken cancellationToken)
            => Task.FromResult<DigitalBrain.Capabilities.Identity.CallDecision?>(new DigitalBrain.Capabilities.Identity.CallDecision { Allowed = true });

        public Task<IReadOnlyList<Grant>> ListGrants(string brainScopeId, CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<Grant>>([]);

        public Task<Grant?> GrantAsOwner(CapabilityCaller caller, Grant grant, CancellationToken cancellationToken)
            => Task.FromResult<Grant?>(grant);

        public Task<bool> RevokeAsOwner(CapabilityCaller caller, string appId, string semanticTypeId, GrantMode mode, CancellationToken cancellationToken)
            => Task.FromResult(true);
    }
}

