using System.Text.Json;
using DigitalBrain;
using DigitalBrain.Capabilities;
using DigitalBrain.Runtime.Orleans.Capabilities;
using DigitalBrain.Testing;
using IntoChat.OS.Authority.Identity.Authority;
using IntoChat.OS.Authority.Identity.Grants;
using IntoChat.OS.Connections.Identity;
using IntoChat.OS.Connections.Identity.Configuration;
using IntoChat.OS.Connections.Platform.Identity;
using IntoChat.OS.Framework;
using IntoChat.OS.Framework.Modules;
using IntoChat.OS.Framework.Modules.Platform.Identity;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace IntoChat.OS.Authority.Identity;

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
            .Register<DirectoryNeuron>()
            .Register<GrantsNeuron>();

        var directoryId = new NeuronId(new BrainId("system"), IDirectoryStore.Type, "main");
        var directory = os.Get<IDirectoryStore>(directoryId);

        // Seed an upgraded 0.11 home where "owner" account exists and "owner" brain has no members:
        await directory.EnsureAccount(new Account { AccountId = "owner", OwnerPrincipalId = "owner", Name = "owner" });
        await directory.EnsureMember(new Member
        {
            PrincipalId = "temp",
            AccountId = "owner",
            BrainId = "owner",
            Role = MemberRole.Member,
            DisplayName = "temp",
        });
        await directory.RemovePrincipalMember("owner", "temp");
        Assert.Null(await directory.Membership("owner", "owner"));

        var identity = new TestIdentityClient(directory);
        var births = new NoOpBrainBirths();
        var bootstrap = new OwnerBootstrap(identity, births, Options.Create(new AuthOptions { Posture = IdentityPosture.Open }), TimeProvider.System);

        var ct = TestContext.Current.CancellationToken;
        await bootstrap.Execute(ct);
        var member = await directory.Membership("owner", "owner");
        Assert.NotNull(member);
        Assert.Equal(MemberRole.Owner, member!.Role);

        await bootstrap.Execute(ct);
        var stateJson = await ((DigitalBrain.Spi.IBrainObserver)os).State(directoryId, ct);
        Assert.NotNull(stateJson);
        var state = stateJson.Value.Deserialize<DirectoryState>(new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
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
            .Register<DirectoryNeuron>()
            .Register<GrantsNeuron>();

        var directoryId = new NeuronId(new BrainId("system"), IDirectoryStore.Type, "main");
        var directory = os.Get<IDirectoryStore>(directoryId);
        var identity = new TestIdentityClient(directory);
        var bootstrap = new OwnerBootstrap(identity, new NoOpBrainBirths(), Options.Create(new AuthOptions { Posture = IdentityPosture.Open }), TimeProvider.System);

        var caller = CapabilityCallers.Of("owner", "owner", "owner", CallerKind.User);

        // Before OwnerBootstrap runs (no Owner membership row in the directory), Open posture alone does not admit:
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            directory.GrantAsOwner(caller, Grant.Tuple("brain:owner", "app:owner/apps/demo", "owner/apps/demo", "os.notes")));

        // After OwnerBootstrap writes the Owner membership row, RequireOwner admits:
        await bootstrap.Execute(TestContext.Current.CancellationToken);
        var grant = await directory.GrantAsOwner(caller, Grant.Tuple("brain:owner", "app:owner/apps/demo", "owner/apps/demo", "os.notes"));
        Assert.Equal("os.notes", grant.Scope);
    }

    private sealed class NoOpBrainBirths : IBrainBirths
    {
        public Task<Member> Born(BrainProvision provision) => Task.FromResult(provision.Member);
    }

    private sealed class TestIdentityClient(IDirectoryStore directory) : IIdentityClient
    {
        public Task<Account?> ReadAccount(string accountId) => directory.ReadAccount(accountId);
        public Task<Account?> OwnerOfBrain(string brainId) => directory.OwnerOfBrain(brainId);
        public Task EnsureAccount(Account account) => directory.EnsureAccount(account);
        public Task<BrainProvision> CreateBrain(string accountId, string principalId, string operationId, string displayName, BrainKind kind)
            => directory.CreateBrain(accountId, principalId, operationId, displayName, kind);
        public Task<BrainProvision> EnsureBrain(Member owner) => directory.EnsureBrain(owner);
        public Task<BrainProvision> ProvisionOwner(Member owner) => directory.ProvisionOwner(owner);
        public Task<bool> IsRegistered(string principalId) => directory.IsRegistered(principalId);
        public Task<BrainProvision> RegisterPrincipal(string principalId, string displayName) => directory.RegisterPrincipal(principalId, displayName);
        public Task<BrainProvision?> CompleteRegistration(string principalId) => directory.CompleteRegistration(principalId);
        public Task<BrainEntry[]> BrainsOf(string principalId) => directory.BrainsOf(principalId);
        public Task<BrainKind> KindOf(string brainScopeId) => directory.KindOf(brainScopeId);
        public Task<bool> BrainExists(string brainScopeId) => directory.BrainExists(brainScopeId);
        public Task<Member?> Membership(string brainScopeId, string principalId) => directory.Membership(brainScopeId, principalId);
        public Task<Member?> EnsureMember(Member member) => directory.EnsureMember(member);
        public Task RemoveMember(string brainScopeId, string accountId) => directory.RemoveMember(brainScopeId, accountId);
        public Task RemovePrincipalMember(string brainScopeId, string principalId) => directory.RemovePrincipalMember(brainScopeId, principalId);
        public Task<CallDecision?> Authorize(CallRequest request) => directory.Authorize(request);
        public Task<Grant[]> ListGrants(string brainScopeId) => directory.ListGrants(brainScopeId);
        public Task<Grant> GrantAsOwner(CapabilityCaller caller, Grant grant) => directory.GrantAsOwner(caller, grant);
        public Task<Grant[]> GrantShipped(string brainScopeId, Grant[] grants) => directory.GrantShipped(brainScopeId, grants);
        public Task RevokeAsOwner(CapabilityCaller caller, string grantId) => directory.RevokeAsOwner(caller, grantId);
        [Obsolete("Use RevokeAsOwner with grantId")]
        public Task RevokeAsOwner(CapabilityCaller caller, string appId, string semanticTypeId, GrantMode mode)
            => directory.RevokeAsOwner(caller, appId, semanticTypeId, mode);
        public Task<BrainProvision> RedeemExternalTicket(string issuer, string email, string? displayName = null)
            => directory.RedeemExternalTicket(issuer, email, displayName);
    }
}
