using System.Text.Json;

namespace DigitalBrain.Platform.Mesh;

// One machine's volunteer shards (T23-1, R23: one silo per machine, so one writer): each shard is a file named by its
// content id's hex, beside a sidecar of the claims on it. Never grain storage and never the part log: these are other
// accounts' ciphertexts, not this machine's state. Quotas (T23-4) and claims (T23-7, R99) are kept here.
internal class ShardStore
{
    public const int MaxClaims = 16;

    private const string Prefix = "sha256:";

    private readonly string _directory;
    private readonly long _offered;
    private readonly long _share;
    private readonly int _maxShardBytes;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<string, (long Bytes, ShardClaim[] Claims)> _held = new(StringComparer.Ordinal);

    public ShardStore(StorageNodeOptions options, int maxShardBytes)
    {
        ArgumentNullException.ThrowIfNull(options);
        _directory = options.Directory ?? throw new InvalidOperationException($"{StorageNodeOptions.Key}:{nameof(StorageNodeOptions.Directory)} is not resolved.");
        (_offered, _share, _maxShardBytes) = (options.OfferedBytes, options.ShareOfAccount, maxShardBytes);
        System.IO.Directory.CreateDirectory(_directory);
        foreach (var claims in System.IO.Directory.EnumerateFiles(_directory, "*.claims"))
        {
            var hex = Path.GetFileNameWithoutExtension(claims);
            var shard = Path.Combine(_directory, hex + ".shard");
            if (!File.Exists(shard)) { File.Delete(claims); continue; }
            _held[Prefix + hex] = (new FileInfo(shard).Length, JsonSerializer.Deserialize<ShardClaim[]>(File.ReadAllBytes(claims)) ?? []);
        }
        foreach (var partial in System.IO.Directory.EnumerateFiles(_directory, "*.tmp")) { File.Delete(partial); }
    }

    public long UsedBytes
    {
        get { lock (_held) { return _held.Values.Sum(shard => shard.Bytes); } }
    }

    // A repeated put of a held shard is never refused: it adds the putting machine's claim. `registered` tells whether a
    // claimant machine is still registered, so a full claim list makes room by its oldest unbound claim (R99).
    public async Task Put(string cid, byte[] bytes, string putterMachine, string[] putterAccounts, Func<string, Task<bool>>? registered = null)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        ArgumentException.ThrowIfNullOrWhiteSpace(putterMachine);
        var hex = Hex(cid);
        if (bytes.Length > _maxShardBytes) { throw new InvalidDataException($"A shard of {bytes.Length} bytes exceeds the {_maxShardBytes}-byte cap."); }
        if (StorageProof.ContentId(bytes) != cid) { throw new InvalidDataException($"Shard bytes do not match content id '{cid}'."); }
        await _gate.WaitAsync();
        try
        {
            var claim = new ShardClaim(putterMachine, putterAccounts ?? []);
            if (_held.TryGetValue(cid, out var held))
            {
                if (held.Claims.Any(existing => existing.MachineId == putterMachine)) { return; }
                var claims = held.Claims;
                if (claims.Length >= MaxClaims)
                {
                    ShardClaim? unbound = null;
                    foreach (var existing in claims)
                    {
                        if (registered is not null && !await registered(existing.MachineId)) { unbound = existing; break; }
                    }
                    // A placement miss for the putter (MeshStore places the shard elsewhere), never a lost claim for a holder.
                    if (unbound is null) { throw new NodeFullException($"Shard {cid} already has {MaxClaims} claims."); }
                    claims = [.. claims.Where(existing => existing != unbound)];
                }
                Save(hex, cid, held.Bytes, [.. claims, claim]);
                return;
            }
            if (UsedBytes + bytes.Length > _offered) { throw new NodeFullException($"This node offers {_offered} bytes; {UsedBytes} are used."); }
            foreach (var account in claim.Accounts)
            {
                if (AccountBytes(account) + bytes.Length > _share)
                { throw new NodeFullException($"Account {account} would hold more than its {_share}-byte share of this node."); }
            }
            var path = Path.Combine(_directory, hex + ".shard");
            var partial = path + ".tmp";
            await File.WriteAllBytesAsync(partial, bytes);
            File.Move(partial, path, overwrite: true);
            Save(hex, cid, bytes.Length, [claim]);
        }
        finally { _gate.Release(); }
    }

    public virtual async Task<byte[]?> Get(string cid)
    {
        var path = Path.Combine(_directory, Hex(cid) + ".shard");
        lock (_held) { if (!_held.ContainsKey(cid)) { return null; } }
        try { return await File.ReadAllBytesAsync(path); }
        catch (FileNotFoundException) { return null; }
    }

    public virtual async Task<byte[]?> Prove(string cid, byte[] nonce)
        => await Get(cid) is { } bytes ? StorageProof.Compute(nonce, bytes) : null;

    // R114: drops the claims of the caller's machine and accounts; a caller without a claim changes nothing. The bytes
    // go with the last claim.
    public async Task Drop(string cid, string callerMachine, string[] callerAccounts)
    {
        var hex = Hex(cid);
        await _gate.WaitAsync();
        try
        {
            if (!_held.TryGetValue(cid, out var held)) { return; }
            var rest = held.Claims.Where(claim => claim.MachineId != callerMachine && !claim.Accounts.Intersect(callerAccounts ?? [], StringComparer.Ordinal).Any()).ToArray();
            if (rest.Length == held.Claims.Length) { return; }
            if (rest.Length > 0) { Save(hex, cid, held.Bytes, rest); return; }
            lock (_held) { _held.Remove(cid); }
            File.Delete(Path.Combine(_directory, hex + ".claims"));
            File.Delete(Path.Combine(_directory, hex + ".shard"));
        }
        finally { _gate.Release(); }
    }

    private long AccountBytes(string account)
    {
        lock (_held) { return _held.Values.Where(shard => shard.Claims.Any(claim => claim.Accounts.Contains(account, StringComparer.Ordinal))).Sum(shard => shard.Bytes); }
    }

    private void Save(string hex, string cid, long bytes, ShardClaim[] claims)
    {
        var path = Path.Combine(_directory, hex + ".claims");
        File.WriteAllBytes(path + ".tmp", JsonSerializer.SerializeToUtf8Bytes(claims));
        File.Move(path + ".tmp", path, overwrite: true);
        lock (_held) { _held[cid] = (bytes, claims); }
    }

    // Only a well-formed content id names a file: nothing a peer sends can reach outside the node's directory.
    private static string Hex(string cid)
    {
        if (cid is null || !cid.StartsWith(Prefix, StringComparison.Ordinal) || cid.Length != Prefix.Length + 64
            || cid.AsSpan(Prefix.Length).ContainsAnyExcept("0123456789abcdef"))
        { throw new InvalidDataException($"'{cid}' is not a shard content id."); }
        return cid[Prefix.Length..];
    }
}

internal sealed record ShardClaim(string MachineId, string[] Accounts);
