using System.Text.Json;
using DigitalBrain;
using DigitalBrain.Access;
using DigitalBrain.Capabilities;
using DigitalBrain.Capabilities.Identity;
using DigitalBrain.Runtime.Orleans.Capabilities;
using DigitalBrain.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using IntoChat.OS.Modules.Identity.Accounts;
using IntoChat.OS.Modules.Identity.Authority;
using IntoChat.OS.Modules.Identity.Directory;
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
        await using var os = TestOS.Local(o => o.Services = services.BuildServiceProvider())
            .Register<IdentityDirectoryGrain>()
            .Register<GrantsNeuron>();

        var directoryId = new NeuronId(new BrainId("system"), IDirectoryStore.Type, "main");
        var directory = os.Get<IDirectoryStore>(directoryId);

        var identity = new TestIdentityClient(directory, os.Edge);

        // Seed an upgraded 0.11 home where "owner" account exists and "owner" brain has no members:
        await identity.EnsureAccount(new Account { AccountId = "owner", OwnerPrincipalId = "owner", Name = "owner" });
        await identity.EnsureMember(new Member
        {
            PrincipalId = "temp",
            AccountId = "owner",
            BrainId = "owner",
            Role = MemberRole.Member,
            DisplayName = "temp",
        });
        await identity.RemovePrincipalMember("owner", "temp");
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
        var state = stateJson.Value.Deserialize<IdentityDirectoryState>(new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
        var brain = Assert.Single(state.Brains, b => b.BrainId == "owner");
        Assert.Single(brain.Members);
    }

    [Fact]
    public async Task OpenOwnerManagesGrantsThroughMembershipOnly()
    {
        var services = new ServiceCollection();
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton(Options.Create(new AuthOptions { Posture = IdentityPosture.Open }));
        services.AddSingleton<AuthorityMirror>();
        await using var os = TestOS.Local(o => o.Services = services.BuildServiceProvider())
            .Register<IdentityDirectoryGrain>()
            .Register<GrantsNeuron>();

        var directoryId = new NeuronId(new BrainId("system"), IDirectoryStore.Type, "main");
        var directory = os.Get<IDirectoryStore>(directoryId);
        var identity = new TestIdentityClient(directory, os.Edge);
        var bootstrap = new OwnerBootstrap(identity, new NoOpBrainBirths(), Options.Create(new AuthOptions { Posture = IdentityPosture.Open }), TimeProvider.System);

        // Before OwnerBootstrap runs (no Owner membership row in the directory), Open posture alone does not admit:
        using (os.Edge.Enter(os.Edge.Person("owner", "owner", new BrainId("owner"))))
        {
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
                directory.GrantAsOwner("owner", Grant.Tuple("brain:owner", "app:owner/apps/demo", "owner/apps/demo", "os.notes")));
        }

        // After OwnerBootstrap writes the Owner membership row, RequireOwner admits:
        await bootstrap.Execute(TestContext.Current.CancellationToken);
        Grant grant;
        using (os.Edge.Enter(os.Edge.Person("owner", "owner", new BrainId("owner"))))
        {
            grant = await directory.GrantAsOwner("owner", Grant.Tuple("brain:owner", "app:owner/apps/demo", "owner/apps/demo", "os.notes"));
        }
        Assert.Equal("os.notes", grant.Scope);

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

    internal sealed class TestIdentityClient(IDirectoryStore directory, EdgeAuthority edge) : IIdentityClient
    {
        // As LocalIdentityClient: the directory is reached as its module, except the owner methods, which keep the stamped owner.
        private async Task<T> AsDirectory<T>(Func<Task<T>> call)
        {
            using var _ = edge.Enter(edge.Module("identity.directory", new BrainId("system")));
            return await call();
        }

        private async Task AsDirectory(Func<Task> call)
        {
            using var _ = edge.Enter(edge.Module("identity.directory", new BrainId("system")));
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
        public Task<Member?> EnsureMember(Member member) => AsDirectory(() => directory.EnsureMember(member));
        public Task RemoveMember(string brainScopeId, string accountId) => AsDirectory(() => directory.RemoveMember(brainScopeId, accountId));
        public Task RemovePrincipalMember(string brainScopeId, string principalId) => AsDirectory(() => directory.RemovePrincipalMember(brainScopeId, principalId));
        public Task<CallDecision?> Authorize(Caller? caller, CallRequest request)
            => BrainAuthorityDecisions.Authorize(caller, request, (brain, grants) => AsDirectory(() => directory.AuthorityOf(brain, grants)),
                (brain, grants, ids) => AsDirectory(() => directory.SpendOnce(brain, grants, ids)));
        public Task<Grant[]> ListGrants(string brainScopeId) => AsDirectory(() => directory.ListGrants(brainScopeId));
        public Task<Grant> GrantAsOwner(string brainId, Grant grant) => directory.GrantAsOwner(brainId, grant);
        public Task<Grant[]> GrantShipped(string brainScopeId, Grant[] grants) => AsDirectory(() => directory.GrantShipped(brainScopeId, grants));
        public Task RevokeAsOwner(string brainId, string grantId) => directory.RevokeAsOwner(brainId, grantId);
        [Obsolete("Use RevokeAsOwner with grantId")]
        public Task RevokeAsOwner(string brainId, string appId, string semanticTypeId, GrantMode mode)
            => directory.RevokeAsOwner(brainId, appId, semanticTypeId, mode);
        public Task<BrainProvision> RedeemExternalTicket(string issuer, string email, string? displayName = null)
            => AsDirectory(() => directory.RedeemExternalTicket(issuer, email, displayName));
    }
}
