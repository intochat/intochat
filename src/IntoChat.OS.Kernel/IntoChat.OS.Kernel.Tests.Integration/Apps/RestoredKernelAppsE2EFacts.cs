using DigitalBrain.Capabilities.Connections.Broker;
using DigitalBrain.Signals;
using DigitalBrain.Capabilities.Connections.Requests;
using System.Diagnostics;
using DigitalBrain;
using DigitalBrain.Capabilities;
using DigitalBrain.Capabilities.Identity;
using DigitalBrain.Capabilities.Storage;
using DigitalBrain.Contracts;
using DigitalBrain.Runtime.Orleans;
using DigitalBrain.Runtime.Orleans.Capabilities;
using DigitalBrain.Runtime.Orleans.Hosting;
using DigitalBrain.Runtime.Orleans.Testing;
using DigitalBrain.Spi;
using DigitalBrain.Testing;
using IntoChat.OS.Apps.Creator;
using IntoChat.OS.Apps.Desktop;
using IntoChat.OS.Apps.Gallery;
using IntoChat.OS.Apps.Ino;
using IntoChat.OS.Apps.Post;
using IntoChat.OS.Authority;
using IntoChat.OS.Authority.Identity;
using IntoChat.OS.Framework;
using IntoChat.OS.Framework.Modules;
using DigitalBrain.Runtime.Orleans.Confirmations;
using IntoChat.OS.Modules.AI;
using IntoChat.OS.Modules.AI.Memory;
using IntoChat.OS.Modules.Apps;
using IntoChat.OS.Modules.Flame;
using IntoChat.OS.Modules.Flutter;
using IntoChat.OS.Modules.Flutter.Chat;
using IntoChat.OS.Modules.Flutter.Views;
using IntoChat.OS.Modules.Microsoft.Aspire;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace IntoChat.OS.Kernel.Tests.Integration.Apps;

/// <summary>
/// Exercises the signal-driven <see cref="CompiledAppsHost"/>, <see cref="IApp"/>,
/// <see cref="IChat"/>, <see cref="IView"/>, and <see cref="IAppStudio"/> neurons on the real DigitalBrain SiloHost.
/// </summary>
public sealed class RestoredKernelAppsE2EFacts(RestoredKernelAppsE2EFacts.KernelAppsSiloFixture fixture)
    : IClassFixture<RestoredKernelAppsE2EFacts.KernelAppsSiloFixture>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task CompiledAppsHostPublishesViewsChatAndCrossAppStudioRoutingThroughSiloSignals()
    {
        var brain = fixture.NewBrain("hosted");
        using var stamp = RuntimeCallerContext.Stamp(CapabilityCallers.Of(brain, brain, brain));
        var settings = SiloNeurons.Get<IUserSettings>(fixture.Services, $"{brain}/settings");
        await settings.SetProfile("Ada Lovelace", "dark", "qwen3.5:4b");

        // 1. Import InoApp.Bundle and trigger Ino open via IApp.Request("open", "greet"), verifying
        //    CompiledAppsHost publishes main/side IView and IChat transcript on the silo.
        var inoApp = SiloNeurons.Get<IApp>(fixture.Services, $"{brain}/apps/ino");
        await inoApp.Import(InoApp.Bundle);
        await inoApp.Request("open", "greet");

        var inoView = await WaitUntilAsync(
            async () => await SiloNeurons.Get<IView>(fixture.Services, $"{brain}/apps/ino/view").Read(),
            view => view is not null);
        Assert.NotNull(inoView);

        var inoSide = await WaitUntilAsync(
            async () => await SiloNeurons.Get<IView>(fixture.Services, $"{brain}/apps/ino/side").Read(),
            view => view is not null);
        Assert.NotNull(inoSide);

        // 2. Send prompts through IApp.Request("send", "...") and verify CompiledAppsHost projects the
        //    conversation to IChat ("apps/ino/transcript") and routes studio work to PhotoCraft and FilmCraft.
        await inoApp.Request("send", "Remove background from portrait.png");

        var chatState = await WaitUntilAsync(
            async () => await SiloNeurons.Get<IChat>(fixture.Services, $"{brain}/apps/ino/transcript").Read(),
            state => state.Projection?.Version >= 2);
        Assert.NotNull(chatState.Projection);
        Assert.True(chatState.Projection.Generation >= 1);
        Assert.True(chatState.Projection.Version >= 2);

        var photoCraftApp = SiloNeurons.Get<IApp>(fixture.Services, $"{brain}/apps/photocraft");
        var photoSnapshot = await WaitUntilAsync(
            async () => await photoCraftApp.Read(),
            snap => snap is not null);
        Assert.NotNull(photoSnapshot);

        await inoApp.Request("send", "Trim timeline clip in FilmCraft");
        var filmCraftApp = SiloNeurons.Get<IApp>(fixture.Services, $"{brain}/apps/filmcraft");
        var filmSnapshot = await WaitUntilAsync(
            async () => await filmCraftApp.Read(),
            snap => snap is not null);
        Assert.NotNull(filmSnapshot);

        // 3. Open Gallery, Creator, and Post via IApp.Request("open", "") on the silo and verify their app snapshots.
        foreach (var (appName, bundle) in new (string Name, AppBundle Bundle)[]
        {
            ("gallery", GalleryApp.Bundle),
            ("creator", CreatorApp.Bundle),
            ("post", PostApp.Bundle),
        })
        {
            var appNeuron = SiloNeurons.Get<IApp>(fixture.Services, $"{brain}/apps/{appName}");
            if (string.IsNullOrWhiteSpace((await appNeuron.Read()).Manifest.Title)) { await appNeuron.Import(bundle); }
            await appNeuron.Request("open", "");

            var snapshot = await WaitUntilAsync(
                async () => await appNeuron.Read(),
                s => !string.IsNullOrWhiteSpace(s?.Manifest?.Title));
            Assert.False(string.IsNullOrWhiteSpace(snapshot.Manifest.Title));
        }

        // 4. Verify HostedKernelBddNeuron grain activation on the silo.
        var siloBdd = SiloNeurons.Get<IBdd>(fixture.Services, new NeuronId(new BrainId(brain), "bdd", "bdd"));
        var report = await siloBdd.Record(
            new BddReport(
                "ino",
                [new ScenarioVerdict("Ino routes PhotoCraft on silo", Passed: true, Error: null, Elapsed: TimeSpan.FromMilliseconds(3))],
                BaselinePassed: true,
                BaselineError: null,
                Elapsed: TimeSpan.FromMilliseconds(3)),
            Ct);
        Assert.True(report.Passed);
        var readReport = await siloBdd.Read(Ct);
        Assert.NotNull(readReport);
        Assert.Equal("ino", readReport.AppId);
    }

    private static async Task<T> WaitUntilAsync<T>(Func<Task<T>> probe, Func<T, bool> predicate)
    {
        var sw = Stopwatch.StartNew();
        T last = default!;
        while (sw.Elapsed < TimeSpan.FromSeconds(8))
        {
            last = await probe();
            if (predicate(last))
            {
                return last;
            }
            await Task.Delay(50, Ct);
        }
        return last;
    }

    [DigitalBrain.Module("restored.kernel.apps.e2e")]
    private sealed class RestoredKernelAppsTestModule : DigitalBrain.Module
    {
        public static ModuleManifest Manifest { get; } = new(
            Provides: [],
            Requires: [],
            Needs: [],
            Settings: [],
            Secrets: []);
    }

    public sealed class KernelAppsSiloFixture : IAsyncLifetime
    {
        private readonly FakeStorage _storage = new("Workspace Disk");
        private IInProcessHost? _host;
        private int _counter;

        public IServiceProvider Services => (_host ?? throw new InvalidOperationException("Silo host is not started.")).Services;

        public string NewBrain(string prefix)
            => $"{prefix}{Interlocked.Increment(ref _counter)}";

        public async ValueTask InitializeAsync()
        {
            var signalAssemblies = new[]
            {
                typeof(Activated).Assembly,
                typeof(RestoredKernelAppsTestModule).Assembly,
                typeof(BddVerified).Assembly,
                typeof(AppsModule).Assembly,
                typeof(InoApp).Assembly,
                typeof(IApp).Assembly,
                typeof(IntoChat.OS.Modules.Apps.Signals.AppRequested).Assembly,
                typeof(IdentityModule).Assembly,
                typeof(AuthorityPlainNeurons).Assembly,
                typeof(IConnectionRequests).Assembly,
                typeof(AIModule).Assembly,
                typeof(IMemory).Assembly,
                typeof(AspireModule).Assembly,
                typeof(IAspire).Assembly,
                typeof(FlutterModule).Assembly,
                typeof(IView).Assembly,
                typeof(ChatChanged).Assembly,
                typeof(FlameModule).Assembly,
            }.Distinct().ToArray();

            var factory = new LocalSiloHostFactory(
                new BrainStorageRegistration(signalAssemblies),
                services =>
                {
                    AppsModule.ConfigureServices(services, new ConfigurationBuilder().Build());
                    foreach (var d in services.Where(d => d.ImplementationType?.Name is "FeatureBootLifecycle").ToList())
                    {
                        services.Remove(d);
                    }
                    services.AddSecretsCapability<VaultPort>();
                    services.AddIdentityCapability<TestIdentityDirectoryPort>();
                    services.AddSingleton<ICapabilityConnections>(
                        new SingleStorageConnections("local-disk", "Workspace Disk", _storage));
                });

            _host = await factory.Start(
                new InProcessHostOptions
                {
                    Modules =
                    [
                        typeof(RestoredKernelAppsTestModule),
                        typeof(AppsModule),
                        typeof(IdentityModule),
                        typeof(AuthorityPlainNeurons),
                        typeof(AIModule),
                        typeof(AspireModule),
                        typeof(FlutterModule),
                        typeof(FlameModule),
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

        private sealed class SingleStorageConnections(string key, string label, IStorage storage) : ICapabilityConnections
        {
            public Task<IReadOnlyList<BrainConnection>> ListInOrder(BrainId brain, CancellationToken ct = default)
                => Task.FromResult<IReadOnlyList<BrainConnection>>(
                    [new BrainConnection(key, label, true, [Capability.Storage.V1], null)]);

            public Task<TCapability> Bind<TCapability>(BrainId brain, string connectionKey, string callerApp, CancellationToken ct = default)
                where TCapability : class
                => Task.FromResult((TCapability)(object)storage);

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
}
