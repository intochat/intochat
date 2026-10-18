namespace IntoChat.OS.Framework.Platform.Identity;

// The narrow slice of account and membership authority a privileged module may use to provision principals,
// brains and members. Resolved only for module ids listed in IntoChatOS:Identity:PrivilegedModules.
public interface IIdentityProvisioning
{
    Task EnsureAccount(Account account);

    Task<BrainProvision> EnsureBrain(Member owner);

    Task<BrainProvision> ProvisionOwner(Member owner);

    Task<bool> IsRegistered(string principalId);

    Task<BrainProvision> RegisterPrincipal(string principalId, string displayName);

    Task<BrainProvision?> CompleteRegistration(string principalId);

    Task<BrainEntry[]> BrainsOf(string principalId);

    Task<Member?> Membership(string brainScopeId, string principalId);

    Task<Member?> EnsureMember(Member member);
}
