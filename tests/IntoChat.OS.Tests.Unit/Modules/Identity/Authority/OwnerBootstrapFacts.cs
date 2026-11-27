using System.Text.Json;
using DigitalBrain;
using DigitalBrain.Access;
using DigitalBrain.Capabilities;
using DigitalBrain.Capabilities.Identity;
using DigitalBrain.Identity.Local.Directory;
using DigitalBrain.Runtime.Orleans.Capabilities;
using DigitalBrain.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using IntoChat.OS.Modules.Identity.Accounts;
using IntoChat.OS.Modules.Identity.Authority;
using IntoChat.OS.Modules.Identity.Grants;
using IntoChat.OS.Modules.Identity.Contracts.Accounts;

namespace IntoChat.OS.Authority.Identity;

[Collection("EdgeGrantsCache")]
public sealed class OwnerBootstrapFacts
{
    [Fact]
    public async Task UpgradedOpenBrainRegainsOwnerOnActivation()
    {
        var services = new ServiceCollection();
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton(Options.Create(new AuthOptions { Posture = IdentityPosture.Open }));
        services.AddSingleton<AuthorityMirror>();
        services.AddSingleton<IDirectoryChangeObserver>(sp => sp.GetRequiredService<AuthorityMirror>());
        await using var os = TestOS.Local(o => o.Services = services.BuildServiceProvider())
            .Register<DirectoryNeuron>()
            .Register<GrantsNeuron>();

        var directoryId = new NeuronId(new BrainId("system"), IDirectory.Type, "main");
        // Seed an upgraded 0.11 home where "owner" account exists and "owner" brain has no members:
        os.SeedStoredState(directoryId, """
            {
              "accounts": [{ "accountId": "owner", "ownerPrincipalId": "owner", "name": "owner" }],
              "principals": [],
              "brains": [{ "brainId": "owner", "accountId": "owner", "members": [], "grants": [], "ownerProvisioned": false, "kind": 0 }],
              "operations": []
            }
            """);
        var directory = os.Get<IDirectory>(directoryId);

        var identity = new TestIdentityClient(directory, os.Edge);
        Assert.Null(await identity.Membership("owner", "owner"));

        var births = new NoOpBrainBirths();
        var bootstrap = new OwnerBootstrap(identity, births, Options.Create(new AuthOptions { Posture = IdentityPosture.Open }), TimeProvider.System);

        var ct = TestContext.Current.CancellationToken;
        await bootstrap.Execute(ct);
        var member = await identity.Membership("owner", "owner");
        Assert.NotNull(member);
        Assert.Equal(MemberRole.Owner, member!.Role);

        await bootstrap.Execute(ct);
        var stateJson = await ((DigitalBrain.Kernel.IBrainObserver)os).State(directoryId, ct);
        Assert.NotNull(stateJson);
        var state = stateJson.Value.Deserialize<DirectoryState>(new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
        Assert.Single(state.Brains, b => b.BrainId == "owner");
        Assert.Single(state.Members, m => m.BrainId == "owner");
    }

    [Fact]
    public async Task OpenOwnerManagesGrantsThroughMembershipOnly()
    {
        var services = new ServiceCollection();
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton(Options.Create(new AuthOptions { Posture = IdentityPosture.Open }));
        services.AddSingleton<AuthorityMirror>();
        services.AddSingleton<IDirectoryChangeObserver>(sp => sp.GetRequiredService<AuthorityMirror>());
        await using var os = TestOS.Local(o => o.Services = services.BuildServiceProvider())
            .Register<DirectoryNeuron>()
            .Register<GrantsNeuron>();

        var directoryId = new NeuronId(new BrainId("system"), IDirectory.Type, "main");
        var directory = os.Get<IDirectory>(directoryId);
        var identity = new TestIdentityClient(directory, os.Edge);
        var bootstrap = new OwnerBootstrap(identity, new NoOpBrainBirths(), Options.Create(new AuthOptions { Posture = IdentityPosture.Open }), TimeProvider.System);

        // Before OwnerBootstrap runs (no Owner membership row in the directory), Open posture alone does not admit:
        using (os.Edge.Enter(os.Edge.Person("owner", "owner", new BrainId("owner"))))
        {
            Assert.Null(await directory.GrantAsOwner("owner", Grant.Tuple("brain:owner", "app:owner/apps/demo", "owner/apps/demo", "os.notes")));
        }

        // After OwnerBootstrap writes the Owner membership row, GrantAsOwner admits:
        await bootstrap.Execute(TestContext.Current.CancellationToken);
        Grant? grant;
        using (os.Edge.Enter(os.Edge.Person("owner", "owner", new BrainId("owner"))))
        {
            grant = await directory.GrantAsOwner("owner", Grant.Tuple("brain:owner", "app:owner/apps/demo", "owner/apps/demo", "os.notes"));
        }
        Assert.NotNull(grant);
        Assert.Equal("os.notes", grant!.Scope);

        // The decision reads the directory's authority state over NeuronProxy and decides from the caller value:
        var appAllowed = await identity.Authorize(
            os.Edge.App("owner/apps/demo", new Actor.Person("owner", "owner"), new BrainId("owner")),
            new CallRequest
            {
                TargetNeuron = "owner/apps/demo",
                Operation = "read",
                SemanticTypeIds = ["os.notes"],
            });
        Assert.NotNull(appAllowed);
        Assert.True(appAllowed!.Allowed);

        var appDenied = await identity.Authorize(
            os.Edge.App("owner/apps/stranger", new Actor.Person("owner", "owner"), new BrainId("owner")),
            new CallRequest
            {
                TargetNeuron = "owner/apps/demo",
                Operation = "read",
                SemanticTypeIds = ["os.notes"],
            });
        Assert.NotNull(appDenied);
        Assert.False(appDenied!.Allowed);
    }

    internal sealed class NoOpBrainBirths : IBrainBirths
    {
        public Task<Member> Born(BrainProvision provision) => Task.FromResult(provision.Member);
    }

    internal sealed class TestIdentityClient(IDirectory directory, EdgeAuthority edge) : IIdentityClient
    {
        // As LocalIdentityClient: platform calls run as the kernel; owner methods keep the stamped owner.
        private async Task<T> AsDirectory<T>(Func<Task<T>> call)
        {
            using var _ = edge.Enter(edge.Kernel(BrainId.Kernel));
            return await call();
        }

        private async Task AsDirectory(Func<Task> call)
        {
            using var _ = edge.Enter(edge.Kernel(BrainId.Kernel));
            await call();
        }

        public Task<Account?> ReadAccount(string accountId) => AsDirectory(() => directory.ReadAccount(accountId));
        public Task<Account?> OwnerOfBrain(string brainId) => AsDirectory(() => directory.OwnerOfBrain(brainId));
        public Task EnsureAccount(Account account) => AsDirectory(() => directory.EnsureAccount(account));
        public Task<BrainProvision> CreateBrain(string accountId, string principalId, string operationId, string displayName, BrainKind kind)
            => AsDirectory(() => directory.CreateBrain(accountId, principalId, operationId, displayName, kind));
        public Task<BrainProvision> EnsureBrain(Member owner) => AsDirectory(() => directory.EnsureBrain(owner));
        public Task<BrainProvision> ProvisionOwner(Member owner) => AsDirectory(() => directory.ProvisionOwner(owner));
        public Task<bool> IsRegistered(string principalId) => AsDirectory(() => directory.IsRegistered(principalId));
        public Task<BrainProvision> RegisterPrincipal(string principalId, string displayName) => AsDirectory(() => directory.RegisterPrincipal(principalId, displayName));
        public Task<BrainProvision?> CompleteRegistration(string principalId) => AsDirectory(() => directory.CompleteRegistration(principalId));
        public Task<BrainEntry[]> BrainsOf(string principalId) => AsDirectory(() => directory.BrainsOf(principalId));
        public Task<BrainKind> KindOf(string brainScopeId) => AsDirectory(() => directory.KindOf(brainScopeId));
        public Task<bool> BrainExists(string brainScopeId) => AsDirectory(() => directory.BrainExists(brainScopeId));
        public Task<Member?> Membership(string brainScopeId, string principalId) => AsDirectory(() => directory.Membership(brainScopeId, principalId));
        public Task<Member?> EnsureMember(Member member) => directory.EnsureMember(member);
        public Task RemoveMember(string brainScopeId, string accountId) => directory.RemoveMember(brainScopeId, accountId);
        public Task RemovePrincipalMember(string brainScopeId, string principalId) => directory.RemovePrincipalMember(brainScopeId, principalId);
        public Task<CallDecision?> Authorize(Caller? caller, CallRequest request)
            => BrainAuthorityDecisions.Authorize(
                caller,
                request,
                (brain, _) => AsDirectory(() => directory.SnapshotOf(brain)),
                (_, grantsBrain, ids) => AsDirectory(() => directory.SpendOnce(grantsBrain, ids)));
        public Task<Grant[]> ListGrants(string brainScopeId) => AsDirectory(() => directory.ListGrants(brainScopeId));
        public async Task<Grant> GrantAsOwner(string brainId, Grant grant)
            => await directory.GrantAsOwner(brainId, grant) ?? throw new UnauthorizedAccessException("Only the current brain owner can manage grants.");
        public Task<Grant[]> GrantShipped(string brainScopeId, Grant[] grants) => AsDirectory(() => directory.GrantShipped(brainScopeId, grants));
        public async Task RevokeAsOwner(string brainId, string grantId)
        {
            if (!await directory.RevokeGrantAsOwner(brainId, grantId))
            {
                throw new UnauthorizedAccessException("Only the current brain owner can manage grants.");
            }
        }
        [Obsolete("Use RevokeAsOwner with grantId")]
        public async Task RevokeAsOwner(string brainId, string appId, string semanticTypeId, GrantMode mode)
        {
            if (!await directory.RevokeAsOwner(brainId, appId, semanticTypeId, mode))
            {
                throw new UnauthorizedAccessException("Only the current brain owner can manage grants.");
            }
        }
    }
}
