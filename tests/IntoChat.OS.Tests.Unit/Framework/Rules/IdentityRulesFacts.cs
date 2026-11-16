using System.Reflection;
using DigitalBrain.Capabilities;
using DigitalBrain.Rules;
using DigitalBrain.Runtime.Orleans.Capabilities;
using IntoChat.OS.Authority.Identity.Authority;
using IntoChat.OS.Authority.Identity.Rules;
using IntoChat.OS.Authority.Integrations;
using IntoChat.OS.Connections.Identity.Configuration;
using IntoChat.OS.Framework.Modules.Platform.Identity;

namespace IntoChat.OS.Tests.Unit.Framework.Rules;

public sealed class IdentityRulesFacts
{
    private static Member MakeMember(string principalId = "alice", MemberRole role = MemberRole.Member, string accountId = "acct-1", string brainId = "brain-1")
        => new()
        {
            PrincipalId = principalId,
            AccountId = accountId,
            BrainId = brainId,
            Role = role,
            DisplayName = principalId,
        };

    [Fact]
    public void PrimaryConstructorsOfIdentityRulesJudgeDomainValuesWithoutBooleanFlags()
    {
        var ruleAssembly = typeof(AccountOwnerMatches).Assembly;
        var identityRuleTypes = ruleAssembly
            .GetTypes()
            .Where(t => typeof(Rule).IsAssignableFrom(t) && !t.IsAbstract && t.Namespace == typeof(AccountOwnerMatches).Namespace)
            .ToArray();

        Assert.NotEmpty(identityRuleTypes);

        foreach (var ruleType in identityRuleTypes)
        {
            var ctors = ruleType.GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            // Primary/domain constructors (excluding compiler copy ctor) must include at least one ctor with zero bool parameters.
            var nonCopyCtors = ctors
                .Where(c => !(c.GetParameters() is { Length: 1 } p && p[0].ParameterType == ruleType))
                .ToArray();
            Assert.Contains(nonCopyCtors, c => c.GetParameters().All(p => p.ParameterType != typeof(bool)));
        }
    }

    [Fact]
    public void IdentityRulesJudgeDomainObjectsAccurately()
    {
        var ownerMember = MakeMember("alice", MemberRole.Owner);
        var regularMember = MakeMember("bob", MemberRole.Member);
        var brain = new BrainRow
        {
            BrainId = "brain-1",
            AccountId = "acct-1",
            Members = [ownerMember, regularMember],
            OwnerProvisioned = true,
            Kind = BrainKind.Personal,
        };

        Assert.True(new AccountOwnerMatches("alice", "bob").Broken);
        Assert.False(new AccountOwnerMatches("alice", "alice").Broken);

        Assert.True(new PrincipalIdIsValid("Invalid!").Broken);
        Assert.False(new PrincipalIdIsValid("alice-1").Broken);

        Assert.False(new PrincipalIdIsAvailable("alice", ExistingPrincipal: null).Broken);
        Assert.True(new PrincipalIdIsAvailable("owner", ExistingPrincipal: null).Broken);
        Assert.True(new PrincipalIdIsAvailable("alice", new PrincipalRow { PrincipalId = "alice", DefaultMembership = ownerMember }).Broken);

        Assert.True(new BrainIsProvisioned("brain-1", Brain: null).Broken);
        Assert.False(new BrainIsProvisioned("brain-1", brain).Broken);

        Assert.True(new MemberIsNotBrainOwner("brain-1", "acct-1", brain, ownerMember).Broken);
        Assert.True(new MemberIsNotBrainOwner("brain-1", "bob", brain, ownerMember).Broken);
        Assert.False(new MemberIsNotBrainOwner("brain-1", "bob", brain, regularMember).Broken);

        Assert.True(new BrainAuthorityIsInitialized(Brain: null).Broken);
        Assert.False(new BrainAuthorityIsInitialized(brain).Broken);

        Assert.True(new MemberIsOwnerRole(regularMember).Broken);
        Assert.False(new MemberIsOwnerRole(ownerMember).Broken);

        Assert.True(new BrainOwnerMatches("brain-1", "alice", "bob").Broken);
        Assert.False(new BrainOwnerMatches("brain-1", "alice", "alice").Broken);

        Assert.True(new BrainHasSingleOwner(ownerMember).Broken);
        Assert.False(new BrainHasSingleOwner(regularMember).Broken);

        Assert.True(new MemberRoleMatches(MemberRole.Owner, MemberRole.Member).Broken);
        Assert.False(new MemberRoleMatches(MemberRole.Owner, MemberRole.Owner).Broken);

        Assert.True(new BrainKindIsMutable(brain).Broken);
        Assert.False(new BrainKindIsMutable(brain with { OwnerProvisioned = false }).Broken);

        Assert.True(new ContactListNameIsValid("Bad List").Broken);
        Assert.False(new ContactListNameIsValid("trusted-peers").Broken);

        Assert.True(new ContactPrincipalIsNotEmpty("   ").Broken);
        Assert.False(new ContactPrincipalIsNotEmpty("bob").Broken);

        Assert.True(new ConnectionBelongsToBrain("brain-2", "brain-1").Broken);
        Assert.False(new ConnectionBelongsToBrain("brain-1", "brain-1").Broken);

        Assert.True(new IntegrationIsAvailable("github", Service: null).Broken);
        Assert.False(new IntegrationIsAvailable("github", new object()).Broken);

        Assert.True(new AuthMethodIsNotBrowserLogin("github", "browser").Broken);
        Assert.False(new AuthMethodIsNotBrowserLogin("github", "token").Broken);

        Assert.True(new ConnectionIsConfigured(Configuration: null).Broken);
        Assert.False(new ConnectionIsConfigured("configured-secret").Broken);

        Assert.True(new BrainSealerIsAvailable(Sealer: null).Broken);
        Assert.False(new BrainSealerIsAvailable(new object()).Broken);

        var connector = new ConnectorCatalogEntry("github", "GitHub", ["token", "oauth"], [], [], "github");
        Assert.True(new AuthMethodIsOffered(connector, "saml").Broken);
        Assert.False(new AuthMethodIsOffered(connector, "token").Broken);

        var ownerCaller = new CapabilityCaller("alice", "acct-1", "brain-1");
        var otherCaller = new CapabilityCaller("mallory", "acct-2", "brain-1");
        var platformCaller = CapabilityCallers.Platform("vault", "system", "brain-1");
        Assert.False(new CallerCanStoreSecret(ownerCaller, "alice").Broken);
        Assert.True(new CallerCanStoreSecret(otherCaller, "alice").Broken);
        Assert.False(new CallerCanRemoveSecret(ownerCaller, "alice").Broken);
        Assert.False(new CallerCanRemoveSecret(platformCaller, "alice").Broken);
        Assert.True(new CallerCanRemoveSecret(otherCaller, "alice").Broken);

        var issuers = new[] { new TrustedIssuer { Name = "https://auth.example" } };
        Assert.False(new IssuerIsKnown("https://auth.example", issuers).Broken);
        Assert.True(new IssuerIsKnown("https://evil.example", issuers).Broken);
    }
}
