using IntoChat.OS.Tests;
using DigitalBrain;
using DigitalBrain.Access;
using DigitalBrain.Capabilities;
using DigitalBrain.Capabilities.Identity;
using DigitalBrain.Identity.Local.Directory;
using DigitalBrain.Rules;
using DigitalBrain.Testing;
using IntoChat.OS.Modules.Identity.Accounts;
using IntoChat.OS.Modules.Identity.Contracts.Accounts;
using IntoChat.OS.Modules.Identity.Rules;

namespace IntoChat.OS.Authority.Tests.Flows;

public sealed class ExternalTicketFlowFacts(IdentityFixture fixture) : IClassFixture<IdentityFixture>
{
    public static TheoryData<NeuronRuntimeKind> Kinds => IdentityFixture.Kinds;

    [Theory, MemberData(nameof(Kinds))]
    public async Task AnExternalTicketForAnUnknownIssuerIsRefusedByRule(NeuronRuntimeKind kind)
    {
        var runtime = await fixture.Runtime(kind);
        // The directory lives in the system brain and is cluster-internal: ticket redemption is platform code.
        using var kernel = runtime.Edge.EnterKernel(new BrainId("system"));
        var directory = runtime.Get<IDirectory>(new NeuronId(new BrainId("system"), IDirectory.Type, "main"));
        IIdentityClient client = new ProxyIdentityClient(directory, [new TrustedIssuer { Name = "known-issuer" }]);

        var ex = await Assert.ThrowsAsync<RuleViolationException>(() => client.RedeemExternalTicket("unknown-issuer", "alice@example.com", "Alice"));
        Assert.Contains("identity.issuer-known", ex.Rejected.Rules);

        var provisioned = await client.RedeemExternalTicket("known-issuer", "alice@example.com", "Alice");
        Assert.Equal(MemberRole.Owner, provisioned.Member.Role);
    }

    private sealed class ProxyIdentityClient(IDirectory directory, IReadOnlyList<TrustedIssuer> trustedIssuers) : IIdentityClient
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
        public Task<CallDecision?> Authorize(Caller? caller, CallRequest request) => throw new NotSupportedException("The ticket flow never authorizes a call.");
        public Task<Grant[]> ListGrants(string brainScopeId) => directory.ListGrants(brainScopeId);
        public async Task<Grant> GrantAsOwner(string brainId, Grant grant)
            => await directory.GrantAsOwner(brainId, grant) ?? throw new UnauthorizedAccessException();
        public Task<Grant[]> GrantShipped(string brainScopeId, Grant[] grants) => directory.GrantShipped(brainScopeId, grants);
        public async Task RevokeAsOwner(string brainId, string grantId)
        {
            if (!await directory.RevokeGrantAsOwner(brainId, grantId)) { throw new UnauthorizedAccessException(); }
        }
        [Obsolete("Use RevokeAsOwner with grantId")]
        public async Task RevokeAsOwner(string brainId, string appId, string semanticTypeId, GrantMode mode)
        {
            if (!await directory.RevokeAsOwner(brainId, appId, semanticTypeId, mode)) { throw new UnauthorizedAccessException(); }
        }
        public async Task<BrainProvision> RedeemExternalTicket(string issuer, string email, string? displayName = null)
        {
            var rule = new IssuerIsKnown(issuer, trustedIssuers);
            if (rule.Broken) { throw new RuleViolationException(new Rejected("RedeemExternalTicket", [rule.Name], [rule.Message])); }
            var normalized = ExternalSignInTicket.NormalizeEmail(email) ?? email.Trim();
            var principalId = IntoChat.OS.Modules.Identity.External.ExternalSignIn.PrincipalOf(issuer, normalized);
            var name = string.IsNullOrWhiteSpace(displayName) ? normalized : displayName.Trim();
            return await directory.CompleteRegistration(principalId) ?? await directory.RegisterPrincipal(principalId, name);
        }
    }
}
