using DigitalBrain;
using DigitalBrain.Framework;
using DigitalBrain.Framework.Platform.Identity;
using DigitalBrain.Platform.Identity.Authority;
using Microsoft.Extensions.Options;
using Orleans.Runtime;

namespace DigitalBrain.Platform.Identity.Directory;

// The identity front door: every operation goes to the directly keyed actor that owns it.
[GrainType("identity-directory")]
internal sealed class IdentityDirectoryNeuron : Neuron, IIdentityDirectory
{
    public async Task<Member> RegisterAsync(string principalId, string password, string displayName, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return await GrainFactory.GetGrain<IPrincipal>(principalId).Register(password, displayName);
    }
    public async Task<Member?> AuthenticateAsync(string principalId, string password, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrEmpty(principalId)) { return null; }
        return await GrainFactory.GetGrain<IPrincipal>(principalId).Authenticate(password);
    }
    public async Task<Member?> FindMemberAsync(string principalId, string accountId, string brainId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return await GrainFactory.GetGrain<IBrainAuthority>(BrainScope.Create(accountId, brainId).Id).Membership(principalId);
    }
    public async Task<bool> CanAccessAsync(string principalId, string accountId, string brainId, CancellationToken cancellationToken = default)
        => await FindMemberAsync(principalId, accountId, brainId, cancellationToken) is not null;
    public async Task<Member> ShareBrainAsync(string accountId, string brainId, string principalId, string displayName, MemberRole role, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var member = new Member { AccountId = accountId, BrainId = brainId, PrincipalId = principalId, DisplayName = displayName, Role = role, JoinedAt = DateTimeOffset.UtcNow };
        var authority = GrainFactory.GetGrain<IBrainAuthority>(BrainScope.Create(accountId, brainId).Id);
        await authority.EnsureMember(member);
        return (await authority.Membership(principalId))!;
    }
}
