using IntoChat.OS;
using IntoChat.OS.Connections;
using IntoChat.OS.Contracts.Configuration;
using IntoChat.OS.Framework.Modules;
using IntoChat.OS.Connections.Platform.Identity;
using IntoChat.OS.Framework.Modules.Platform.Identity;
using Orleans;
using Orleans.Runtime;

namespace IntoChat.OS.Authority.Identity.Authority;

[GenerateSerializer, Alias("identity.principal-state.v2")]
internal sealed record PrincipalState
{
    [Id(0)] public Member? DefaultMembership { get; init; }
    [Id(2)] public bool RegistrationComplete { get; init; }
    // The brains this principal is a member of, as their authorities announce it.
    [Id(3)] public BrainEntry[] Brains { get; init; } = [];
}

[GrainType("identity.principal.v2")]
internal sealed class PrincipalGrain(
    [PersistentState("principal", IntoChatOSNames.PlatformGrainStorage)] IPersistentState<PrincipalState> storage,
    TimeProvider clock) : IdentityStateGrain<PrincipalState>(storage), IPrincipal
{
    private AuthorityNeurons Platform => new(GrainFactory);

    // Password hashes are held by PrincipalPasswords.
    public async Task<BrainProvision> Register(string displayName)
    {
        var principal = this.GetPrimaryKeyString();
        if (principal.Length is 0 or > 80 || !System.Text.RegularExpressions.Regex.IsMatch(principal, "^[a-z0-9][a-z0-9-]*$"))
        { throw new ArgumentException("Use a lowercase username containing letters, numbers and hyphens."); }
        if (principal is "owner" or "integrations" || State.DefaultMembership is not null)
        { throw new InvalidOperationException("That username is unavailable."); }
        var accountId = Guid.NewGuid().ToString("N");
        var member = new Member
        {
            PrincipalId = principal,
            AccountId = accountId,
            BrainId = "account-" + accountId,
            DisplayName = displayName,
            Role = MemberRole.Owner,
            JoinedAt = clock.GetUtcNow()
        };
        await Persist(new() { DefaultMembership = member });
        return (await CompleteRegistration())!;
    }

    public Task<bool> IsRegistered() => Task.FromResult(State.RegistrationComplete);


    // Idempotent: a completed registration answers its membership again, with no new brain.
    public async Task<BrainProvision?> CompleteRegistration()
    {
        if (State.DefaultMembership is not { } member) { return null; }
        if (State.RegistrationComplete) { return new(member, false); }
        var account = Platform.Account(member.AccountId);
        await account.EnsureCreated(new Account
        {
            AccountId = member.AccountId,
            OwnerPrincipalId = member.PrincipalId,
            Name = member.DisplayName,
            CreatedAt = member.JoinedAt
        });
        var created = await account.EnsureBrain(member);
        await Persist(State with { RegistrationComplete = true });
        return new(member, created);
    }

    public Task<Member?> DefaultMembership() => Task.FromResult(State.DefaultMembership);

    public Task<BrainEntry[]> Brains() => Task.FromResult(State.Brains);

    public Task Joined(string brainId, BrainKind kind) => Join(brainId, kind);

    public Task Join(string brainId, BrainKind kind)
        => State.Brains.Any(known => known.Id == brainId) ? Task.CompletedTask : Persist(State with { Brains = [.. State.Brains, new BrainEntry(brainId, kind)] });

    public Task Left(string brainId)
        => State.Brains.Any(known => known.Id == brainId) ? Persist(State with { Brains = [.. State.Brains.Where(known => known.Id != brainId)] }) : Task.CompletedTask;
}
