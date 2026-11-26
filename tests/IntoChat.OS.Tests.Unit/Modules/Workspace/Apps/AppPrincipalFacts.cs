using System.Collections.Concurrent;
using System.Text.Json;
using DigitalBrain;
using DigitalBrain.Access;
using DigitalBrain.Capabilities;
using DigitalBrain.Capabilities.Identity;
using DigitalBrain.Runtime.Orleans.Capabilities;
using DigitalBrain.Testing;
using IntoChat.OS.Modules.Identity.Accounts;
using IntoChat.OS.Modules.Identity.Authority;
using IntoChat.OS.Modules.Identity.Contracts.Accounts;
using IntoChat.OS.Modules.Identity.Directory;
using IntoChat.OS.Modules.Identity.Grants;
using IntoChat.OS.Modules.Identity.Ports;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace IntoChat.OS.Modules.Apps.Tests.Unit.Apps;

public sealed class AppPrincipalFacts
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly AppOrigin Shipped = new(ShippedApps.Publisher, new AppSource.Shipped(), "notes", "1.0.0");

    private static AppManifest Manifest(string title = "Notes") => new(title, "Keep notes", [], [], []);

    private static AppBundle Bundle(string name = "notes") => new(name, "1.0.0", Manifest(), "spec", []);

    [Fact]
    public async Task ShippedInstallUnderKernelOnEphemeralBrain_EmitsZeroWarnings_AndCreatesNoDurableMembershipRows()
    {
        var logs = new RecordingLoggerProvider();
        var ephemeral = new EphemeralBrainRegistry(TimeProvider.System, Options.Create(new EphemeralBrainOptions()));
        var record = ephemeral.Mint();

        await using var harness = await CreateHarnessAsync(logs, ephemeral);

        var brainId = new BrainId(record.BrainId);
        var appKey = $"{record.BrainId}/apps/notes";

        using (harness.Os.Edge.Enter(harness.Os.Edge.Kernel(brainId)))
        {
            var app = harness.Os.Get<IApp>(new NeuronId(brainId, IntoChat.OS.Host.Primitives.AppGrainType.Name, "apps/notes"));
            await app.Import(Bundle(), Shipped);
            await app.Delete();
        }

        Assert.DoesNotContain(logs.Entries, e => e.Level >= LogLevel.Warning && e.Category.Contains("AppNeuron", StringComparison.Ordinal));
        Assert.Null(await harness.Identity.Membership(record.BrainId, AppPrincipal.Id(appKey)));

        var directoryId = new NeuronId(new BrainId("system"), IDirectoryStore.Type, "main");
        var stateJson = await ((DigitalBrain.Kernel.IBrainObserver)harness.Os).State(directoryId, Ct);
        if (stateJson is not null)
        {
            var state = stateJson.Value.Deserialize<IdentityDirectoryState>(new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
            Assert.DoesNotContain(state.Brains, b => b.BrainId == record.BrainId);
        }
    }

    [Fact]
    public async Task NonOwnerCallers_SkipAppPrincipalChanges_WithoutWarnings()
    {
        var logs = new RecordingLoggerProvider();
        await using var harness = await CreateHarnessAsync(logs, ephemeral: null);

        const string brainScope = "brain-alice";
        await SeedDurableOwnerAsync(harness.Identity, "alice", "acct-alice", brainScope);

        var brainId = new BrainId(brainScope);
        Caller[] nonOwners =
        [
            harness.Os.Edge.Kernel(brainId),
            harness.Os.Edge.Agent(new Actor.Person("alice", "acct-alice"), brainId),
            harness.Os.Edge.App($"{brainScope}/apps/helper", new Actor.Person("alice", "acct-alice"), brainId),
            harness.Os.Edge.Module("apps.catalog", brainId),
            harness.Os.Edge.Scheduler(brainId),
            harness.Os.Edge.Person("bob", "acct-bob", brainId),
        ];

        var index = 0;
        foreach (var caller in nonOwners)
        {
            var appName = $"app{index++}";
            var appKey = $"{brainScope}/apps/{appName}";
            using (harness.Os.Edge.Enter(caller))
            {
                var app = harness.Os.Get<IApp>(new NeuronId(brainId, IntoChat.OS.Host.Primitives.AppGrainType.Name, $"apps/{appName}"));
                await app.Create(Manifest(appName), "spec");
                await app.Delete();
            }
            Assert.Null(await harness.Identity.Membership(brainScope, AppPrincipal.Id(appKey)));
        }

        Assert.DoesNotContain(logs.Entries, e => e.Level >= LogLevel.Warning && e.Category.Contains("AppNeuron", StringComparison.Ordinal));
    }

    [Fact]
    public async Task OwnerInitiatedInstallAndDelete_EnsureAndRemoveAppPrincipalCleanly()
    {
        var logs = new RecordingLoggerProvider();
        await using var harness = await CreateHarnessAsync(logs, ephemeral: null);

        const string brainScope = "brain-alice";
        await SeedDurableOwnerAsync(harness.Identity, "alice", "acct-alice", brainScope);

        var brainId = new BrainId(brainScope);
        var appKey = $"{brainScope}/apps/notes";
        var appPrincipal = AppPrincipal.Id(appKey);

        using (harness.Os.Edge.Enter(harness.Os.Edge.Person("alice", "acct-alice", brainId)))
        {
            var app = harness.Os.Get<IApp>(new NeuronId(brainId, IntoChat.OS.Host.Primitives.AppGrainType.Name, "apps/notes"));
            await app.Import(Bundle(), Shipped);

            var member = await harness.Identity.Membership(brainScope, appPrincipal);
            Assert.NotNull(member);
            Assert.Equal(MemberRole.App, member!.Role);
            Assert.Equal("acct-alice", member.AccountId);

            // Deleting as a non-owner must not log a warning or throw.
            using (harness.Os.Edge.Enter(harness.Os.Edge.Kernel(brainId)))
            {
                await app.Delete();
            }
            // App principal still exists because Kernel did not invoke RemovePrincipalMember.
            Assert.NotNull(await harness.Identity.Membership(brainScope, appPrincipal));

            // Re-importing and deleting as the owner removes the app principal cleanly.
            await app.Import(Bundle(), Shipped);
            await app.Delete();
            Assert.Null(await harness.Identity.Membership(brainScope, appPrincipal));
        }

        Assert.DoesNotContain(logs.Entries, e => e.Level >= LogLevel.Warning && e.Category.Contains("AppNeuron", StringComparison.Ordinal));
    }

    private static async Task SeedDurableOwnerAsync(IIdentityClient identity, string principalId, string accountId, string brainId)
    {
        await identity.EnsureAccount(new Account { AccountId = accountId, OwnerPrincipalId = principalId, Name = principalId });
        await identity.EnsureBrain(new Member
        {
            PrincipalId = principalId,
            AccountId = accountId,
            BrainId = brainId,
            Role = MemberRole.Owner,
            DisplayName = principalId,
            JoinedAt = DateTimeOffset.UtcNow,
        });
    }

    private static Task<Harness> CreateHarnessAsync(RecordingLoggerProvider logs, EphemeralBrainRegistry? ephemeral)
    {
        var services = new ServiceCollection();
        services.AddOptions<AppsOptions>();
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton(Options.Create(new AuthOptions { Posture = IdentityPosture.Open }));
        services.AddSingleton<AuthorityMirror>();
        services.AddLogging(builder => builder.SetMinimumLevel(LogLevel.Trace).AddProvider(logs));

        var sp = services.BuildServiceProvider();
        var os = TestOS.Local(o => o.Services = sp)
            .Register<AppNeuron>()
            .Register<IdentityDirectoryGrain>()
            .Register<GrantsNeuron>();

        var directoryId = new NeuronId(new BrainId("system"), IDirectoryStore.Type, "main");
        var directory = os.Get<IDirectoryStore>(directoryId);
        IIdentityClient identityClient = new EphemeralAwareTestIdentityClient(directory, os.Edge, ephemeral);
        var port = new DirectoryPort(identityClient, os.Callers);
        var bound = new BoundIdentity(port, os.Callers);
        os.With<IIdentity>(bound);

        return Task.FromResult(new Harness(os, sp, identityClient));
    }

    private sealed class Harness(TestOS os, ServiceProvider sp, IIdentityClient identity) : IAsyncDisposable
    {
        public TestOS Os => os;
        public IIdentityClient Identity => identity;

        public async ValueTask DisposeAsync()
        {
            await os.DisposeAsync();
            sp.Dispose();
        }
    }

    private sealed class EphemeralAwareTestIdentityClient(
        IDirectoryStore directory,
        EdgeAuthority edge,
        EphemeralBrainRegistry? ephemeral) : IIdentityClient
    {
        private readonly IntoChat.OS.Authority.Identity.OwnerBootstrapFacts.TestIdentityClient inner = new(directory, edge);

        public Task<Account?> ReadAccount(string accountId)
            => ephemeral is not null && ephemeral.TryGet(accountId, out var rec)
                ? Task.FromResult<Account?>(rec.Account)
                : inner.ReadAccount(accountId);

        public Task<Account?> OwnerOfBrain(string brainId)
            => ephemeral is not null && ephemeral.TryGet(brainId, out var rec)
                ? Task.FromResult<Account?>(rec.Account)
                : inner.OwnerOfBrain(brainId);

        public Task EnsureAccount(Account account) => inner.EnsureAccount(account);
        public Task<BrainProvision> CreateBrain(string accountId, string principalId, string operationId, string displayName, BrainKind kind)
            => inner.CreateBrain(accountId, principalId, operationId, displayName, kind);
        public Task<BrainProvision> EnsureBrain(Member owner) => inner.EnsureBrain(owner);
        public Task<BrainProvision> ProvisionOwner(Member owner) => inner.ProvisionOwner(owner);
        public Task<bool> IsRegistered(string principalId) => inner.IsRegistered(principalId);
        public Task<BrainProvision> RegisterPrincipal(string principalId, string displayName) => inner.RegisterPrincipal(principalId, displayName);
        public Task<BrainProvision?> CompleteRegistration(string principalId) => inner.CompleteRegistration(principalId);
        public Task<BrainEntry[]> BrainsOf(string principalId)
            => ephemeral is not null && ephemeral.TryGet(principalId, out var rec)
                ? Task.FromResult<BrainEntry[]>([new BrainEntry(rec.BrainId, BrainKind.Personal)])
                : inner.BrainsOf(principalId);
        public Task<BrainKind> KindOf(string brainScopeId)
            => ephemeral is not null && ephemeral.Contains(brainScopeId)
                ? Task.FromResult(BrainKind.Personal)
                : inner.KindOf(brainScopeId);
        public Task<bool> BrainExists(string brainScopeId)
            => ephemeral is not null && ephemeral.Contains(brainScopeId)
                ? Task.FromResult(true)
                : inner.BrainExists(brainScopeId);
        public Task<Member?> Membership(string brainScopeId, string principalId)
            => ephemeral is not null && ephemeral.TryGet(brainScopeId, out var rec)
                ? Task.FromResult<Member?>(string.Equals(rec.Owner.PrincipalId, principalId, StringComparison.Ordinal) ? rec.Owner : null)
                : inner.Membership(brainScopeId, principalId);
        public Task<Member?> EnsureMember(Member member)
            => ephemeral is not null && ephemeral.Contains(member.BrainId)
                ? Task.FromResult<Member?>(null)
                : inner.EnsureMember(member);
        public Task RemoveMember(string brainScopeId, string accountId)
            => ephemeral is not null && ephemeral.Contains(brainScopeId)
                ? Task.CompletedTask
                : inner.RemoveMember(brainScopeId, accountId);
        public Task RemovePrincipalMember(string brainScopeId, string principalId)
            => ephemeral is not null && ephemeral.Contains(brainScopeId)
                ? Task.CompletedTask
                : inner.RemovePrincipalMember(brainScopeId, principalId);
        public Task<CallDecision?> Authorize(Caller? caller, CallRequest request) => inner.Authorize(caller, request);
        public Task<Grant[]> ListGrants(string brainScopeId) => inner.ListGrants(brainScopeId);
        public Task<Grant> GrantAsOwner(string brainId, Grant grant) => inner.GrantAsOwner(brainId, grant);
        public Task<Grant[]> GrantShipped(string brainScopeId, Grant[] grants) => inner.GrantShipped(brainScopeId, grants);
        public Task RevokeAsOwner(string brainId, string grantId) => inner.RevokeAsOwner(brainId, grantId);
        [Obsolete("Use RevokeAsOwner with grantId")]
        public Task RevokeAsOwner(string brainId, string appId, string semanticTypeId, GrantMode mode)
            => inner.RevokeAsOwner(brainId, appId, semanticTypeId, mode);
    }

    private sealed record LogEntry(LogLevel Level, string Category, string Message, Exception? Exception);

    private sealed class RecordingLoggerProvider : ILoggerProvider
    {
        private readonly ConcurrentQueue<LogEntry> entries = new();

        public IReadOnlyList<LogEntry> Entries => [.. entries];

        public ILogger CreateLogger(string categoryName) => new Logger(categoryName, entries);

        public void Dispose() { }

        private sealed class Logger(string category, ConcurrentQueue<LogEntry> entries) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => true;
            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
                => entries.Enqueue(new LogEntry(logLevel, category, formatter(state, exception), exception));
        }
    }
}
