using DigitalBrain.Access;
using System.Diagnostics;
using DigitalBrain;
using DigitalBrain.Capabilities;
using DigitalBrain.Capabilities.Connections.Broker;
using DigitalBrain.Capabilities.Connections.Requests;
using DigitalBrain.Capabilities.Identity;
using DigitalBrain.Capabilities.Storage;
using DigitalBrain.Contracts;
using DigitalBrain.Hosting;
using DigitalBrain.Modules;
using DigitalBrain.Runtime.Orleans;
using DigitalBrain.Runtime.Orleans.Capabilities;
using DigitalBrain.Runtime.Orleans.Hosting;
using DigitalBrain.Runtime.Orleans.Testing;
using DigitalBrain.Signals;
using DigitalBrain.Testing;
using IntoChat.OS.Apps.Creator;
using IntoChat.OS.Apps.Desktop;
using IntoChat.OS.Apps.FilmCraft;
using IntoChat.OS.Apps.Gallery;
using IntoChat.OS.Apps.Ino;
using IntoChat.OS.Apps.PhotoCraft;
using IntoChat.OS.Authority;
using IntoChat.OS.Modules.AI;
using IntoChat.OS.Modules.AI.Memory;
using IntoChat.OS.Modules.Apps;
using IntoChat.OS.Modules.Flutter;
using IntoChat.OS.Modules.Flutter.Views;
using IntoChat.Aspire;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using IntoChat.OS.Modules.Identity;

namespace IntoChat.OS.Kernel.Tests.Integration.Apps;

/// <summary>
/// Exercises <see cref="IApp"/> and <see cref="IUserSettings"/>
/// grains on the real DigitalBrain SiloHost environment without legacy CompiledAppsHost shims.
/// </summary>
public sealed class RestoredKernelAppsE2EFacts(RestoredKernelAppsE2EFacts.KernelAppsSiloFixture fixture)
    : IClassFixture<RestoredKernelAppsE2EFacts.KernelAppsSiloFixture>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task SiloHostExecutesUserSettingsAndAppManifestLifecycle()
    {
        var brain = fixture.NewBrain("hosted");
        var edge = fixture.Services.GetRequiredService<EdgeAuthority>();
        using var stamp = edge.Enter(edge.Person(brain, brain, new BrainId(brain)));
        var settings = SiloNeurons.Get<IUserSettings>(fixture.Services, $"{brain}/settings");
        var updatedProfile = await settings.SetProfile("Ada Lovelace", "dark", "qwen3.5:4b");
        Assert.Equal("Ada Lovelace", updatedProfile.DisplayName);
        Assert.Equal("dark", updatedProfile.Theme);
        Assert.Equal("qwen3.5:4b", updatedProfile.DefaultModel);

        var persistedSettings = await settings.Read();
        Assert.Equal("Ada Lovelace", persistedSettings.DisplayName);
        Assert.Equal("dark", persistedSettings.Theme);
        Assert.Equal("qwen3.5:4b", persistedSettings.DefaultModel);

        // 1. Import InoApp.Bundle and verify IApp manifest & snapshot on the silo.
        var inoApp = SiloNeurons.Get<IApp>(fixture.Services, $"{brain}/apps/ino");
        await inoApp.Import(InoApp.Bundle);
        await inoApp.Request("open", "greet");

        var inoSnapshot = await WaitUntilAsync(
            async () => await inoApp.Read(),
            snap => !string.IsNullOrWhiteSpace(snap?.Manifest?.Title));
        Assert.NotNull(inoSnapshot);
        Assert.Equal("Ino", inoSnapshot.Manifest.Title);
        Assert.Equal(AppStatus.Live, inoSnapshot.Status);

        // 2. Import PhotoCraft and FilmCraft app bundles on the silo and verify snapshots.
        var photoCraftApp = SiloNeurons.Get<IApp>(fixture.Services, $"{brain}/apps/photocraft");
        await photoCraftApp.Import(PhotoCraftApp.Bundle);
        var photoSnapshot = await WaitUntilAsync(
            async () => await photoCraftApp.Read(),
            snap => !string.IsNullOrWhiteSpace(snap?.Manifest?.Title));
        Assert.NotNull(photoSnapshot);
        Assert.Equal("PhotoCraft", photoSnapshot.Manifest.Title);
        Assert.Equal(AppStatus.Live, photoSnapshot.Status);

        var filmCraftApp = SiloNeurons.Get<IApp>(fixture.Services, $"{brain}/apps/filmcraft");
        await filmCraftApp.Import(FilmCraftApp.Bundle);
        var filmSnapshot = await WaitUntilAsync(
            async () => await filmCraftApp.Read(),
            snap => !string.IsNullOrWhiteSpace(snap?.Manifest?.Title));
        Assert.NotNull(filmSnapshot);
        Assert.Equal("FilmCraft", filmSnapshot.Manifest.Title);
        Assert.Equal(AppStatus.Live, filmSnapshot.Status);

        // 3. Open Gallery and Creator via IApp on the silo and verify their app snapshots.
        foreach (var (appName, bundle) in new (string Name, AppBundle Bundle)[]
        {
            ("gallery", GalleryApp.Bundle),
            ("creator", CreatorApp.Bundle),
        })
        {
            var appNeuron = SiloNeurons.Get<IApp>(fixture.Services, $"{brain}/apps/{appName}");
            await appNeuron.Import(bundle);
            await appNeuron.Request("open", "");

            var snapshot = await WaitUntilAsync(
                async () => await appNeuron.Read(),
                s => !string.IsNullOrWhiteSpace(s?.Manifest?.Title));
            Assert.False(string.IsNullOrWhiteSpace(snapshot.Manifest.Title));
            Assert.Equal(AppStatus.Live, snapshot.Status);
        }
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

    [DigitalBrain.Modules.Module("restored.kernel.apps.e2e")]
    private sealed class RestoredKernelAppsTestModule : DigitalBrain.Modules.Module
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
            }.Distinct().ToArray();

            var factory = new LocalSiloHostFactory(
                new BrainStorageRegistration(signalAssemblies),
                services =>
                {
                    AppsModule.ConfigureServices(services, new ConfigurationBuilder().Build());
                    IntoChat.OS.Modules.Authoring.AuthoringModule.ConfigureServices(services, new ConfigurationBuilder().Build());
                    foreach (var d in services.Where(d => d.ImplementationType?.Name is "FeatureBootLifecycle").ToList())
                    {
                        services.Remove(d);
                    }
                    services.AddSingleton<DigitalBrain.Identity.Local.Custody.IMasterKeyCustody>(
                        new DigitalBrain.Identity.Local.Custody.ConfiguredMasterKeyCustody(System.Text.Encoding.UTF8.GetBytes("test-restored-apps-master-key")));
                    IntoChat.OS.Modules.Identity.Secrets.MasterKeyServiceCollectionExtensions.AddOsSecretVaultPort(services);
                    services.AddSecretsCapability<DigitalBrain.Identity.Local.Vault.LocalVaultPort>();
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
                        typeof(IntoChat.OS.Modules.Runs.RunsModule),
                        typeof(IntoChat.OS.Modules.Authoring.AuthoringModule),
                        typeof(IntoChat.OS.Modules.Diagnostics.DiagnosticsModule),
                        typeof(IdentityModule),
                        typeof(AuthorityPlainNeurons),
                        typeof(AIModule),
                        typeof(AspireModule),
                        typeof(FlutterModule),
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
}
