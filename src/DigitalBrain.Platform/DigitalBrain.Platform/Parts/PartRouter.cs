using System.Collections.Concurrent;
using DigitalBrain.Framework.Parts;
using DigitalBrain.Platform.Sequencer;
using DigitalBrain.Platform.Trust;
using Microsoft.Extensions.Options;

namespace DigitalBrain.Platform.Parts;

internal abstract record PartRoute
{
    public sealed record Local : PartRoute;

    public sealed record Remote(string MachineId) : PartRoute;

    public static readonly Local Here = new();
}

// T20-10: where a call for a part runs. Open here → here; else the head's live holder; no live holder → here only
// when this machine may lease the part (R82: the core needs an Own machine), else 503. A remote resolution is
// cached until the head's LeaseExpiry (R89) or a NotHolder reply.
internal sealed class PartRouter(PartLogs logs, ISequencerClient sequencer, MachineIdentity machine, IOptions<PartsOptions> options, TimeProvider time)
    : DigitalBrain.Framework.IStandInActivations
{
    private readonly ConcurrentDictionary<string, (PartRoute.Remote Route, DateTimeOffset Until)> _remote = new(StringComparer.Ordinal);

    public async Task<PartRoute> Resolve(PartId part)
    {
        ArgumentNullException.ThrowIfNull(part);
        if (logs.Opened.Contains(part)) { return PartRoute.Here; }
        var now = time.GetUtcNow();
        if (_remote.TryGetValue(part.Value, out var cached) && cached.Until > now) { return cached.Route; }
        HeadRecord? head;
        try { head = await sequencer.Get(HeadRecord.HashOf(part.Value)); }
        catch (Exception unreachable) when (unreachable is not (ServiceUnavailableException or UnauthorizedAccessException))
        { throw new ServiceUnavailableException($"Part {part}'s holder cannot be resolved: the sequencer is unreachable ({unreachable.Message})."); }
        if (head is not null && head.LeaseExpiry > now)
        {
            if (head.Holder == machine.MachineId) { return PartRoute.Here; }
            var remote = new PartRoute.Remote(head.Holder);
            _remote[part.Value] = (remote, head.LeaseExpiry);
            return remote;
        }
        if (part.IsCore && options.Value.Role != MachineRole.Own)
        { throw new ServiceUnavailableException($"Part {part} has no live holder, and this {options.Value.Role} machine may not hold the core."); }
        return PartRoute.Here;
    }

    public void Forget(PartId part) => _remote.TryRemove(part.Value, out _);

    // R101: activations made here for a part held elsewhere (no state read); they only forward, then go.
    private readonly ConcurrentDictionary<GrainId, bool> _standIns = new();

    public void MarkStandIn(GrainId grain) => _standIns[grain] = true;

    public bool IsStandIn(GrainId grain) => _standIns.ContainsKey(grain);

    public void ForgetStandIn(GrainId grain) => _standIns.TryRemove(grain, out _);

    // Calls a stand-in is forwarding: it is deactivated after the last of them, never under one.
    private readonly ConcurrentDictionary<GrainId, int> _inFlight = new();

    public void Enter(GrainId grain) => _inFlight.AddOrUpdate(grain, 1, (_, count) => count + 1);

    // True when this was the last call in flight.
    public bool Leave(GrainId grain)
    {
        var left = _inFlight.AddOrUpdate(grain, 0, (_, count) => count - 1);
        return left <= 0 && _inFlight.TryRemove(new KeyValuePair<GrainId, int>(grain, left));
    }
}
