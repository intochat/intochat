using IntoChat.OS.Authority.Keys;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Orleans.Serialization;

namespace IntoChat.OS.Authority.Storage;

/// <summary>One stored record of an offline brain read: its grain, state name and the serialized state bytes.</summary>
public sealed record BrainStoreRecord(string GrainId, string StateName, byte[] Record);

/// <summary>An offline brain read: the active segment format, whether a snapshot pointer exists, every record and every journalled signal.</summary>
public sealed record BrainStoreRead(BrainLogFormat Format, bool HasSnapshot, IReadOnlyList<BrainStoreRecord> Records, IReadOnlyList<Signal> Signals);

/// <summary>
/// Read access to a brain store laid out on disk, for tools that open an existing store (storage fixtures, migration
/// checks). It never mints a key: the brain's wrapped key must already exist. The caller supplies the seal root the
/// store was written under, so it learns nothing it could not derive itself. Opening a log claims its writer lock and
/// may truncate a torn tail, so callers read a copy, never live or checked-in files.
/// </summary>
public static class BrainStoreLayout
{
    /// <summary>The directory a brain's log and wrapped key live in under <paramref name="brainStore"/>.</summary>
    public static string DirectoryOf(string brainStore, string brainId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(brainStore);
        return Keys(brainStore, []).DirectoryOf(brainId);
    }

    /// <summary>Opens the brain's existing log under the configured-custody <paramref name="sealRoot"/> and reads it whole.</summary>
    /// <exception cref="FileNotFoundException">The brain has no wrapped key.</exception>
    public static async Task<BrainStoreRead> ReadAsync(byte[] sealRoot, string brainStore, string brainId, Serializer serializer, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(sealRoot);
        ArgumentNullException.ThrowIfNull(serializer);
        ArgumentException.ThrowIfNullOrWhiteSpace(brainStore);
        var keys = Keys(brainStore, sealRoot);
        var directory = keys.DirectoryOf(brainId);
        // GetOrCreate would mint a key for a missing brain; this reader refuses instead.
        if (!File.Exists(Path.Combine(directory, "brain.key"))) { throw new FileNotFoundException($"Brain {brainId} has no brain.key.", directory); }
        var key = await keys.GetOrCreate(brainId, ct).ConfigureAwait(false);
        await using var log = new BrainLog(directory, brainId, key, serializer);
        var records = new List<BrainStoreRecord>();
        foreach (var (grainId, stateName) in (await log.RecordKeys().ConfigureAwait(false)).OrderBy(pair => pair.GrainId, StringComparer.Ordinal).ThenBy(pair => pair.StateName, StringComparer.Ordinal))
        {
            records.Add(new(grainId, stateName, (await log.Read(grainId, stateName).ConfigureAwait(false))!.Record));
        }
        var signals = await log.ReadSignals(0, int.MaxValue, null, ct).ConfigureAwait(false);
        return new(log.Format, File.Exists(Path.Combine(directory, "current")), records, [.. signals.Select(signal => signal.Signal)]);
    }

    /// <summary>Writes a running host's brain state into a snapshot and starts a fresh segment in its write format.</summary>
    public static async Task CheckpointAsync(IServiceProvider services, string brainId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(services);
        var log = await services.GetRequiredService<BrainLogs>().For(brainId).WaitAsync(ct).ConfigureAwait(false);
        await log.Checkpoint(ct).ConfigureAwait(false);
    }

    private static BrainStorageKeys Keys(string brainStore, byte[] sealRoot)
        => new(new ConfiguredMasterKeyCustody(sealRoot), Options.Create(new BrainStoreOptions { Directory = brainStore }));
}
