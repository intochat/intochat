using System.Collections.Concurrent;
using DigitalBrain.Framework.Mesh;
using DigitalBrain.Framework.Parts;
using DigitalBrain.Framework.Platform.Identity;
using DigitalBrain.Platform.Keys;
using DigitalBrain.Platform.Sequencer;
using DigitalBrain.Platform.Trust;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orleans.Serialization;

namespace DigitalBrain.Platform.Parts;

// The part logs this silo holds: one per leased part, opened on first use and reopened once closed (a lost
// lease or an ambiguous commit). The owner account that wraps a part's key comes from the trust side, the
// brain's authority, never from the brain neuron: the brain's own record lives in the part it would unlock.
// A circle brain's parts open under its circle part keys instead (T26-5), and only on an own machine.
internal sealed class PartLogs(TrustDeployment deployment, ISequencerClient sequencer, IKeyringClient keyring, IMeshStore mesh, IPartKeys keys, MachineIdentity machine, SeenVersions seen,
    Serializer serializer, IOptions<PartsOptions> options, TimeProvider time, ILoggerFactory logging, IIdentityClient identity, CirclePartKeys circleKeys,
    LeasePreference? preference = null, IServiceProvider? services = null) : IAsyncDisposable
{
    private readonly ConcurrentDictionary<string, Lazy<Task<PartLog>>> _logs = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, string> _owners = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, BrainKind> _kinds = new(StringComparer.Ordinal);

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

    // The parts this silo holds: every log opened and not closed.
    public PartId[] Opened =>
        [.. _logs.Values.Where(opening => opening.IsValueCreated && opening.Value.IsCompletedSuccessfully && !opening.Value.Result.Closed)
            .Select(opening => opening.Value.Result.Part)];

    // R89: whether this silo's own lease on the part is still live, without opening it or asking the sequencer: what an
    // Own holder trusts while the trust side is unreachable.
    public bool HoldsLocally(PartId part) =>
        _logs.TryGetValue(part.Value, out var opening) && opening.IsValueCreated && opening.Value.IsCompletedSuccessfully && opening.Value.Result.LeaseLive;

    // Hands the part back: its activations here are deactivated so none serves state another machine may change,
    // the log flushes, and the lease is released so another machine can take it. A part this silo does not hold
    // is a no-op.
    public async Task Release(PartId part, Func<string[], Task>? deactivate = null)
    {
        if (!_logs.TryRemove(part.Value, out var opening) || !opening.IsValueCreated) { return; }
        PartLog log;
        try { log = await opening.Value; }
        catch (Exception error) when (error is not OperationCanceledException) { return; }
        if (deactivate is not null) { await deactivate(log.GrainIds); }
        await log.DisposeAsync();
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
        // The deployment folder first: the lease reads and writes SeenVersions inside it.
        var directory = await deployment.Resolve();
        // Q17/R107/R144: a quiet-return deactivates this machine's activations of the part, exactly as a release does.
        var deactivate = services?.GetService<PartActivations>() is { } activations
            ? (Func<string[], Task>)(recorded => activations.Deactivate(part, recorded))
            : null;
        var operatorAccount = preference is null ? null : await keyring.OwnerOf(part.BrainId);
        if (await Kind(part.BrainId) == BrainKind.Circle) { return await OpenCircle(part, directory, operatorAccount, deactivate); }
        var key = await keys.Key(part, await Owner(part.BrainId));
        var lease = await PartLease.Acquire(part, key, sequencer, machine, seen, options.Value, time, serializer, preference);
        return await PartLog.Open(part, key, lease, mesh, serializer, options.Value, time, logging.CreateLogger<PartLog>(), CancellationToken.None, directory, preference, operatorAccount, deactivate);
    }

    // The current key first, then the older ones this machine holds: a head another holder left sealed before a
    // rotation opens under the key it was sealed with and is re-sealed under the current one at once.
    private async Task<PartLog> OpenCircle(PartId part, string directory, string? operatorAccount, Func<string[], Task>? deactivate)
    {
        if (options.Value.Role != MachineRole.Own)
        { throw new UnauthorizedAccessException($"Part {part} belongs to circle {part.BrainId}; only the circle's contributed own machines hold it."); }
        var current = await circleKeys.Unwrap(part, part.BrainId);
        byte[][] candidates = [current, .. (await circleKeys.Held(part, part.BrainId)).Where(held => !held.AsSpan().SequenceEqual(current))];
        for (var i = 0; ; i++)
        {
            PartLease lease;
            try { lease = await PartLease.Acquire(part, candidates[i], sequencer, machine, seen, options.Value, time, serializer, preference); }
            catch (System.Security.Cryptography.CryptographicException) when (i < candidates.Length - 1) { continue; }
            var log = await PartLog.Open(part, candidates[i], lease, mesh, serializer, options.Value, time, logging.CreateLogger<PartLog>(), CancellationToken.None, directory, preference, operatorAccount, deactivate);
            if (i > 0) { await log.Reseal(current); }
            return log;
        }
    }

    private async Task<BrainKind> Kind(string brainId)
    {
        if (_kinds.TryGetValue(brainId, out var cached)) { return cached; }
        return _kinds.GetOrAdd(brainId, await identity.KindOf(brainId));
    }

    private async Task<string> Owner(string brainId)
    {
        if (_owners.TryGetValue(brainId, out var cached)) { return cached; }
        var owner = await keyring.OwnerOf(brainId)
            ?? throw new ServiceUnavailableException($"Brain {brainId} has no owning account yet; its parts cannot be opened.");
        return _owners.GetOrAdd(brainId, owner);
    }
}
