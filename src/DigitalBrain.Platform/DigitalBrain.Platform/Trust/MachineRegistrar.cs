using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using DigitalBrain.Platform.Parts;
using DigitalBrain.Platform.Sequencer;
using Microsoft.Extensions.Options;

namespace DigitalBrain.Platform.Trust;

// Binds this machine to each account it serves (R55), once per account per silo: at start for the host's own
// account, and whenever a brain is born here. The self-signature proves the key; the pairing secret's hash lets
// a second machine of the deployment join an account the first one bound (R52).
internal sealed class MachineRegistrar(INodeDirectoryClient directory, MachineIdentity machine, IOptions<PartsOptions> options)
{
    private readonly ConcurrentDictionary<string, Lazy<Task>> _bound = new(StringComparer.Ordinal);

    public async Task Bind(string account)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(account);
        var binding = _bound.GetOrAdd(account, _ => new Lazy<Task>(() => Register(account)));
        try { await binding.Value; }
        catch
        {
            _bound.TryRemove(new(account, binding));
            throw;
        }
    }

    public static byte[] HashOf(string? pairingSecret)
        => pairingSecret is { Length: > 0 } secret ? SHA256.HashData(Encoding.UTF8.GetBytes(secret)) : [];

    private Task Register(string account)
    {
        var pairing = HashOf(options.Value.PairingSecret);
        var signature = machine.Sign(Signing.Registration(machine.MachineId, machine.PublicKey, account, pairing));
        return directory.RegisterMachine(machine.MachineId, machine.PublicKey, account, pairing, signature);
    }
}
