using DigitalBrain;
using DigitalBrain.Access;
using DigitalBrain.Capabilities;
using DigitalBrain.Capabilities.Identity;
using DigitalBrain.Rules;
using DigitalBrain.Testing;
using IntoChat.OS.Authority.Identity.Authority;
using IntoChat.OS.Authority.Identity.Grants;
using IntoChat.OS.Connections.Identity;
using IntoChat.OS.Connections.Platform.Identity;
using IntoChat.OS.Framework;
using IntoChat.OS.Framework.Modules.Platform.Identity;

namespace IntoChat.OS.Authority.Tests.Flows;

public sealed class ExternalTicketFlowFacts(IdentityFixture fixture) : IClassFixture<IdentityFixture>
{
    public static TheoryData<NeuronRuntimeKind> Kinds => IdentityFixture.Kinds;

    [Theory, MemberData(nameof(Kinds))]
    public async Task AnExternalTicketForAnUnknownIssuerIsRefusedByRule(NeuronRuntimeKind kind)
    {
        var runtime = await fixture.Runtime(kind);
        var directory = runtime.Get<IDirectoryStore>(new NeuronId(new BrainId("system"), IDirectoryStore.Type, "main"));
        IIdentityClient client = new ProxyIdentityClient(directory);

        var ex = await Assert.ThrowsAsync<RuleViolationException>(() => client.RedeemExternalTicket("unknown-issuer", "alice@example.com", "Alice"));
        Assert.Contains("identity.issuer-known", ex.Rejected.Rules);

        var provisioned = await client.RedeemExternalTicket("known-issuer", "alice@example.com", "Alice");
        Assert.Equal(MemberRole.Owner, provisioned.Member.Role);
    }

    private sealed class ProxyIdentityClient(IDirectoryStore directory) : IIdentityClient
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
        public Task<CallDecision?> Authorize(Caller? caller, CallRequest request) => directory.Authorize(DirectoryAuthorizeRequest.From(caller, request));
        public Task<Grant[]> ListGrants(string brainScopeId) => directory.ListGrants(brainScopeId);
        public Task<Grant> GrantAsOwner(string brainId, Grant grant) => directory.GrantAsOwner(brainId, grant);
        public Task<Grant[]> GrantShipped(string brainScopeId, Grant[] grants) => directory.GrantShipped(brainScopeId, grants);
        public Task RevokeAsOwner(string brainId, string grantId) => directory.RevokeAsOwner(brainId, grantId);
        [Obsolete("Use RevokeAsOwner with grantId")]
        public Task RevokeAsOwner(string brainId, string appId, string semanticTypeId, GrantMode mode)
            => directory.RevokeAsOwner(brainId, appId, semanticTypeId, mode);
        public Task<BrainProvision> RedeemExternalTicket(string issuer, string email, string? displayName = null)
            => directory.RedeemExternalTicket(issuer, email, displayName);
    }
}
