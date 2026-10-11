using DigitalBrain.Platform.Sequencer;

namespace DigitalBrain.Platform.Trust;

// How this machine signs the trust calls that act for an account (R37, R63, R65), whichever way they travel (the
// silo's own grains or the trust link): one at a time, so the trust side sees its counter strictly rising.
internal sealed class TrustSigner(MachineIdentity? machine, TimeProvider time, MachineCounter? counter)
{
    private readonly SemaphoreSlim _signing = new(1, 1);

    public MachineIdentity Machine => machine ?? throw new InvalidOperationException("This host is not a machine; it signs no trust call.");

    public async Task<TResult> Signed<T, TResult>(T payload, string account, Func<MachineSigned<T>, Task<TResult>> send)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(account);
        ArgumentNullException.ThrowIfNull(send);
        await _signing.WaitAsync();
        try
        {
            var now = time.GetUtcNow();
            var sequence = (counter ?? throw new InvalidOperationException("This host is not a machine; it signs no trust call.")).Next();
            var signature = Machine.Sign(MachineSigned<T>.SignedBytes(payload, account, Machine.MachineId, now, sequence));
            return await send(new MachineSigned<T>(payload, account, Machine.MachineId, now, sequence, signature));
        }
        finally { _signing.Release(); }
    }

    public Task Signed<T>(T payload, string account, Func<MachineSigned<T>, Task> send)
        => Signed<T, bool>(payload, account, async signed => { await send(signed); return true; });

    // R52: what the machine signs to bind itself to an account.
    public byte[] Registration(string account, byte[] pairingSecretHash)
        => Machine.Sign(Signing.Registration(Machine.MachineId, Machine.PublicKey, Machine.AgreementKey, account, pairingSecretHash));
}
