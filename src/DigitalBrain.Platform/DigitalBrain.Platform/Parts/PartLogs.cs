using System.Collections.Concurrent;
using DigitalBrain.Framework.Mesh;
using DigitalBrain.Framework.Parts;
using DigitalBrain.Platform.Sequencer;
using DigitalBrain.Platform.Trust;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orleans.Serialization;

namespace DigitalBrain.Platform.Parts;

// The part logs this silo holds: one per leased part, opened on first use and reopened once closed (a lost
// lease or an ambiguous commit). The owner account that wraps a part's key comes from the trust side, the
// brain's authority, never from the brain neuron: the brain's own record lives in the part it would unlock.
internal sealed class PartLogs(ISequencerClient sequencer, IKeyringClient keyring, IMeshStore mesh, IPartKeys keys, MachineIdentity machine, SeenVersions seen,
    Serializer serializer, IOptions<PartsOptions> options, TimeProvider time, ILoggerFactory logging) : IAsyncDisposable
{
    private readonly ConcurrentDictionary<string, Lazy<Task<PartLog>>> _logs = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, string> _owners = new(StringComparer.Ordinal);

    public async Task<PartLog> For(PartId part)
    {
        while (true)
        {
            var opening = _logs.GetOrAdd(part.Value, _ => new Lazy<Task<PartLog>>(() => Open(part)));
            PartLog log;
            try { log = await opening.Value; }
            catch
            {
                _logs.TryRemove(new(part.Value, opening));
                throw;
            }
            if (!log.Closed) { return log; }
            if (_logs.TryRemove(new(part.Value, opening))) { await log.DisposeAsync(); }
        }
    }

    // The logs this silo has open for a brain: its core and every app part touched since the silo started.
    public async Task<PartLog[]> OpenFor(string brainId)
    {
        List<PartLog> open = [];
        foreach (var (key, opening) in _logs)
        {
            if (!(key.StartsWith(brainId + "/", StringComparison.Ordinal) && opening.IsValueCreated)) { continue; }
            try { open.Add(await opening.Value); }
            catch (Exception error) { logging.CreateLogger<PartLogs>().LogWarning(error, "Part log {Part} failed to open and is left out of brain {Brain}'s open logs.", key, brainId); }
        }
        return [.. open.Where(log => !log.Closed)];
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var opening in _logs.Values)
        {
            if (!opening.IsValueCreated) { continue; }
            try { await (await opening.Value).DisposeAsync(); }
            catch (Exception error) { logging.CreateLogger<PartLogs>().LogWarning(error, "A part log could not close cleanly."); }
        }
        _logs.Clear();
    }

    private async Task<PartLog> Open(PartId part)
    {
        var key = await keys.Key(part, await Owner(part.BrainId));
        var lease = await PartLease.Acquire(part, key, sequencer, machine, seen, options.Value, time, serializer);
        return await PartLog.Open(part, key, lease, mesh, serializer, options.Value, time, logging.CreateLogger<PartLog>(), CancellationToken.None);
    }

    private async Task<string> Owner(string brainId)
    {
        if (_owners.TryGetValue(brainId, out var cached)) { return cached; }
        var owner = await keyring.OwnerOf(brainId)
            ?? throw new ServiceUnavailableException($"Brain {brainId} has no owning account yet; its parts cannot be opened.");
        return _owners.GetOrAdd(brainId, owner);
    }
}
