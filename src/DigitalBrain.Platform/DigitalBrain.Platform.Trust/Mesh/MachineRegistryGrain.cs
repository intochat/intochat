using DigitalBrain.Framework;
using DigitalBrain.Platform.Sequencer;
using DigitalBrain.Platform.Trust;

namespace DigitalBrain.Platform.Mesh;

// One machine's registration in the node directory, keyed by machine id (R41): its public key, trusted on first
// use, the accounts it serves (R55), and the last counter of a signed call it made (replay).
internal interface IMachineRegistry : IGrainWithStringKey
{
    Task Register(byte[] publicKey, string account, byte[] pairingSecretHash, byte[] selfSignature);

    // The account a signed call acts for, once the signature, binding, timestamp and counter hold.
    Task<string> Accept(SignedCall call);

    // A signed call that acts for no bound account yet (a registration): signature, timestamp and counter only.
    Task AcceptMachine(SignedCall call);

    // What a peer or the trust link checks a machine against (T20-2 check 5): null until it registered.
    [Orleans.Concurrency.AlwaysInterleave]
    Task<RegisteredMachine?> Read();

    // Where the machine says it can be reached (T20-6), signed by it (counter and timestamp as any signed call).
    Task Announce(SignedCall call, MachineCandidates candidates);
}

// One account's machines (R52): the first machine to bind it stores the pairing secret's hash; a later machine
// joins only with the same, non-empty hash.
internal interface IAccountMachines : IGrainWithStringKey
{
    Task Bind(string machineId, byte[] pairingSecretHash);
}

[GrainType("platform.mesh.machine")]
internal sealed class MachineRegistryGrain(
    [PersistentState("machine", DigitalBrainNames.PlatformGrainStorage)] IPersistentState<MachineRegistryState> store,
    TimeProvider time) : Grain, IMachineRegistry
{
    public static readonly TimeSpan Skew = TimeSpan.FromMinutes(5);

    private string MachineId => this.GetPrimaryKeyString();

    public async Task Register(byte[] publicKey, string account, byte[] pairingSecretHash, byte[] selfSignature)
    {
        ArgumentNullException.ThrowIfNull(publicKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(account);
        ArgumentNullException.ThrowIfNull(pairingSecretHash);
        if (MachineKeys.IdOf(publicKey) != MachineId
            || !MachineKeys.Verify(publicKey, Signing.Registration(MachineId, publicKey, account, pairingSecretHash), selfSignature))
        { throw new UnauthorizedAccessException($"Machine {MachineId} did not sign its registration."); }
        if (store.State.PublicKey is { } known && !known.AsSpan().SequenceEqual(publicKey))
        { throw new UnauthorizedAccessException($"Machine {MachineId} is registered with another key."); }
        if (store.State.PublicKey is not null && store.State.Accounts.Contains(account, StringComparer.Ordinal)) { return; }
        await GrainFactory.GetGrain<IAccountMachines>(account).Bind(MachineId, pairingSecretHash);
        store.State.PublicKey = publicKey;
        store.State.Accounts = [.. store.State.Accounts.Append(account).Distinct(StringComparer.Ordinal)];
        await store.WriteStateAsync();
    }

    public Task<RegisteredMachine?> Read() => Task.FromResult(store.State.PublicKey is { } key
        ? new RegisteredMachine(MachineId, key, [.. store.State.Accounts], store.State.Candidates)
        : null);

    public async Task Announce(SignedCall call, MachineCandidates candidates)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        await AcceptMachine(call);
        store.State.Candidates = candidates;
        await store.WriteStateAsync();
    }

    public async Task<string> Accept(SignedCall call)
    {
        ArgumentNullException.ThrowIfNull(call);
        if (!store.State.Accounts.Contains(call.Account, StringComparer.Ordinal))
        { throw new UnauthorizedAccessException($"Machine {MachineId} does not serve account {call.Account}."); }
        await AcceptMachine(call);
        return call.Account;
    }

    public async Task AcceptMachine(SignedCall call)
    {
        ArgumentNullException.ThrowIfNull(call);
        if (call.MachineId != MachineId || store.State.PublicKey is not { } key || !MachineKeys.Verify(key, call.SignedBytes, call.Signature))
        { throw new UnauthorizedAccessException($"The call is not signed by registered machine {MachineId}."); }
        if ((call.Timestamp - time.GetUtcNow()).Duration() > Skew)
        { throw new UnauthorizedAccessException($"The signed call from machine {MachineId} is stale."); }
        if (call.Counter <= store.State.LastCounter)
        { throw new UnauthorizedAccessException($"The signed call from machine {MachineId} replays counter {call.Counter}."); }
        store.State.LastCounter = call.Counter;
        await store.WriteStateAsync();
    }
}

[GrainType("platform.mesh.account-machines")]
internal sealed class AccountMachinesGrain(
    [PersistentState("account-machines", DigitalBrainNames.PlatformGrainStorage)] IPersistentState<AccountMachinesState> store)
    : Grain, IAccountMachines
{
    public async Task Bind(string machineId, byte[] pairingSecretHash)
    {
        if (store.State.Machines.Contains(machineId, StringComparer.Ordinal)) { return; }
        if (store.State.Machines.Length > 0
            && (pairingSecretHash.Length == 0 || !pairingSecretHash.AsSpan().SequenceEqual(store.State.PairingSecretHash)))
        { throw new UnauthorizedAccessException($"Account {this.GetPrimaryKeyString()} is bound to other machines; pairing needs its secret."); }
        if (store.State.Machines.Length == 0) { store.State.PairingSecretHash = pairingSecretHash; }
        store.State.Machines = [.. store.State.Machines, machineId];
        await store.WriteStateAsync();
    }
}

[GenerateSerializer, Alias("platform.mesh.machine-state")]
internal sealed class MachineRegistryState
{
    [Id(0)] public byte[]? PublicKey { get; set; }
    [Id(1)] public string[] Accounts { get; set; } = [];
    [Id(2)] public long LastCounter { get; set; }
    [Id(4)] public MachineCandidates? Candidates { get; set; }
}

// Where a machine may be reached directly (T20-6): host:port strings, announced signed by the machine.
[GenerateSerializer, Alias("platform.mesh.machine-candidates")]
internal sealed record MachineCandidates([property: Id(0)] string[] Endpoints, [property: Id(1)] DateTimeOffset At);

// A machine as the trust side knows it: its key, the accounts it serves (R55) and its announced candidates.
[GenerateSerializer, Alias("platform.mesh.registered-machine")]
internal sealed record RegisteredMachine(
    [property: Id(0)] string MachineId,
    [property: Id(1)] byte[] PublicKey,
    [property: Id(2)] string[] Accounts,
    [property: Id(3)] MachineCandidates? Candidates);

[GenerateSerializer, Alias("platform.mesh.account-machines-state")]
internal sealed class AccountMachinesState
{
    [Id(0)] public byte[] PairingSecretHash { get; set; } = [];
    [Id(1)] public string[] Machines { get; set; } = [];
}
