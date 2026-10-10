using DigitalBrain;
using DigitalBrain.Framework;
using DigitalBrain.Framework.Platform.Identity;
using Orleans;
using Orleans.Runtime;

namespace DigitalBrain.Platform.Identity.Authority;

[GenerateSerializer, Alias("identity.principal-state.v2")]
internal sealed record PrincipalState
{
    [Id(0)] public Member? DefaultMembership { get; init; }
    [Id(2)] public bool RegistrationComplete { get; init; }
    // The brains this principal is a member of, as their authorities announce it.
    [Id(3)] public string[] Brains { get; init; } = [];
    // The machine that registered this principal; only it completes the registration or signs it in (R67).
    [Id(4)] public string? RegisteredBy { get; init; }
}

[GrainType("identity.principal.v2")]
internal sealed class PrincipalGrain(
    [PersistentState("principal", DigitalBrainNames.PlatformGrainStorage)] IPersistentState<PrincipalState> storage,
    TimeProvider clock) : IdentityStateGrain<PrincipalState>(storage), IPrincipal
{
    // No credential is held here (R62): the machine keeps the password hash.
    public async Task<BrainProvision> Register(string displayName, string machineId)
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
        await Persist(new() { DefaultMembership = member, RegisteredBy = machineId });
        return (await CompleteRegistration(machineId))!;
    }

    public Task<bool> IsRegistered() => Task.FromResult(State.RegistrationComplete);

    public Task<string?> RegisteredBy() => Task.FromResult(State.RegisteredBy);

    // Idempotent: a completed registration answers its membership again, with no new brain.
    public async Task<BrainProvision?> CompleteRegistration(string machineId)
    {
        if (State.DefaultMembership is not { } member) { return null; }
        if (State.RegisteredBy != machineId)
        { throw new UnauthorizedAccessException($"Principal {this.GetPrimaryKeyString()} was registered by another machine."); }
        if (State.RegistrationComplete) { return new(member, false); }
        var account = GrainFactory.GetGrain<IAccount>(member.AccountId);
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

    public Task<string[]> Brains() => Task.FromResult(State.Brains);

    public Task Joined(string brainId)
        => State.Brains.Contains(brainId, StringComparer.Ordinal) ? Task.CompletedTask : Persist(State with { Brains = [.. State.Brains, brainId] });

    public Task Left(string brainId)
        => State.Brains.Contains(brainId, StringComparer.Ordinal) ? Persist(State with { Brains = [.. State.Brains.Where(known => known != brainId)] }) : Task.CompletedTask;
}
