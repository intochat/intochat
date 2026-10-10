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
        if (MachineIdentity.IdOf(publicKey) != MachineId
            || !MachineIdentity.Verify(publicKey, Signing.Registration(MachineId, publicKey, account, pairingSecretHash), selfSignature))
        { throw new UnauthorizedAccessException($"Machine {MachineId} did not sign its registration."); }
        if (store.State.PublicKey is { } known && !known.AsSpan().SequenceEqual(publicKey))
        { throw new UnauthorizedAccessException($"Machine {MachineId} is registered with another key."); }
        if (store.State.PublicKey is not null && store.State.Accounts.Contains(account, StringComparer.Ordinal)) { return; }
        await GrainFactory.GetGrain<IAccountMachines>(account).Bind(MachineId, pairingSecretHash);
        store.State.PublicKey = publicKey;
        store.State.Accounts = [.. store.State.Accounts.Append(account).Distinct(StringComparer.Ordinal)];
        await store.WriteStateAsync();
    }

    public async Task<string> Accept(SignedCall call)
    {
        ArgumentNullException.ThrowIfNull(call);
        if (call.MachineId != MachineId || store.State.PublicKey is not { } key || !MachineIdentity.Verify(key, call.SignedBytes, call.Signature))
        { throw new UnauthorizedAccessException($"The call is not signed by registered machine {MachineId}."); }
        if (!store.State.Accounts.Contains(call.Account, StringComparer.Ordinal))
        { throw new UnauthorizedAccessException($"Machine {MachineId} does not serve account {call.Account}."); }
        if ((call.Timestamp - time.GetUtcNow()).Duration() > Skew)
        { throw new UnauthorizedAccessException($"The signed call from machine {MachineId} is stale."); }
        if (call.Counter <= store.State.LastCounter)
        { throw new UnauthorizedAccessException($"The signed call from machine {MachineId} replays counter {call.Counter}."); }
        store.State.LastCounter = call.Counter;
        await store.WriteStateAsync();
        return call.Account;
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
}

[GenerateSerializer, Alias("platform.mesh.account-machines-state")]
internal sealed class AccountMachinesState
{
    [Id(0)] public byte[] PairingSecretHash { get; set; } = [];
    [Id(1)] public string[] Machines { get; set; } = [];
}
