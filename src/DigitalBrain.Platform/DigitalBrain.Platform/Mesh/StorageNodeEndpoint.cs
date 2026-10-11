using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using DigitalBrain.Framework.Links;
using DigitalBrain.Platform.Links;
using DigitalBrain.Platform.Trust;
using Orleans.Serialization;

namespace DigitalBrain.Platform.Mesh;

// The node's side of the machine link (T23-2, T23-7), modelled on ForwardedCallEndpoint: the putter is the link's
// authenticated machine (T20-2), never a field, and its accounts are the trust side's record of it (R55), read once per
// link for a short while. Any registered machine may put, get and prove (ciphertext only); a drop removes only the
// caller's own account's claims (R114). Refusals cross as the reply's error type (InvalidDataException, NodeFullException).
[System.Runtime.Versioning.SupportedOSPlatform("windows"), System.Runtime.Versioning.SupportedOSPlatform("linux"), System.Runtime.Versioning.SupportedOSPlatform("macos")]
internal sealed class StorageNodeEndpoint(ShardStore store, INodeDirectoryClient directory, Serializer serializer, TimeProvider time)
{
    private static readonly TimeSpan Remembered = TimeSpan.FromSeconds(30);

    private readonly ConditionalWeakTable<QuicMachineLink, StrongBox<(string[] Accounts, DateTimeOffset Until)>> _accounts = [];
    private readonly ConcurrentDictionary<string, (bool Registered, DateTimeOffset Until)> _registered = new(StringComparer.Ordinal);

    public ShardStore Store => store;

    public async Task<LinkReply> Answer(QuicMachineLink link, LinkRequest request, CancellationToken _)
    {
        ArgumentNullException.ThrowIfNull(link);
        ArgumentNullException.ThrowIfNull(request);
        var payload = request.Operation switch
        {
            NodeOperations.Put => serializer.SerializeToArray(await Put(link, Arg<ShardPut>(request))),
            NodeOperations.Get => serializer.SerializeToArray(await store.Get(Arg<ShardGet>(request).Cid)),
            NodeOperations.Prove => serializer.SerializeToArray(await Prove(Arg<ShardProve>(request))),
            NodeOperations.Drop => serializer.SerializeToArray(await Drop(link, Arg<ShardDrop>(request))),
            _ => throw new InvalidOperationException($"A storage node answers no '{request.Operation}'."),
        };
        return new LinkReply(payload, null, null);
    }

    private T Arg<T>(LinkRequest request) => serializer.Deserialize<T>(request.Payload) ?? throw new InvalidDataException($"An empty {typeof(T).Name}.");

    private async Task<bool> Put(QuicMachineLink link, ShardPut put)
    {
        await store.Put(put.Cid, put.Bytes, link.RemoteMachineId, await AccountsOf(link), Registered);
        return true;
    }

    private Task<byte[]?> Prove(ShardProve prove) => store.Prove(prove.Cid, prove.Nonce);

    private async Task<bool> Drop(QuicMachineLink link, ShardDrop drop)
    {
        await store.Drop(drop.Cid, link.RemoteMachineId, await AccountsOf(link));
        return true;
    }

    private async Task<string[]> AccountsOf(QuicMachineLink link)
    {
        var now = time.GetUtcNow();
        var box = _accounts.GetOrCreateValue(link);
        var (accounts, until) = box.Value;
        if (accounts is not null && until > now) { return accounts; }
        accounts = (await directory.Machine(link.RemoteMachineId))?.Accounts
            ?? throw new UnauthorizedAccessException($"Machine {link.RemoteMachineId} is not registered; a storage node serves registered machines only.");
        box.Value = (accounts, now + Remembered);
        return accounts;
    }

    private async Task<bool> Registered(string machineId)
    {
        var now = time.GetUtcNow();
        if (_registered.TryGetValue(machineId, out var known) && known.Until > now) { return known.Registered; }
        var registered = await directory.Machine(machineId) is { Accounts.Length: > 0 };
        _registered[machineId] = (registered, now + Remembered);
        return registered;
    }
}
