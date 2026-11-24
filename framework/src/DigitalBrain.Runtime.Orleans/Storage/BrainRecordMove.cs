using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Serialization;

namespace DigitalBrain.Runtime.Orleans;

/// <summary>Selects records to move: a neuron type (any state of every neuron of that type), or a grain id prefix, optionally one state.</summary>
/// <param name="Neuron">A neuron type alias, for example <c>lobby.visitor</c>; matches neuron host records whose key names that type.</param>
/// <param name="Grain">A grain id prefix, for example <c>circle-server.machine-credential/</c>, for plain grains on the brain store.</param>
/// <param name="State">One state name only; null for every state of the matched grains.</param>
public sealed record BrainRecordRule(string? Neuron = null, string? Grain = null, string? State = null)
{
    internal bool Matches(string grain, string state)
    {
        if (State is { } only && !string.Equals(only, state, StringComparison.Ordinal)) { return false; }
        if (Grain is { Length: > 0 } prefix && grain.StartsWith(prefix, StringComparison.Ordinal)) { return true; }
        if (Neuron is not { Length: > 0 } type || !grain.StartsWith("neuron.", StringComparison.Ordinal)) { return false; }
        // A neuron host's grain id is "<host type>/<brain>/<neuron type>/<key>".
        var parts = grain.Split('/', 4);
        return parts.Length >= 3 && string.Equals(parts[2], type, StringComparison.Ordinal);
    }
}

/// <summary>
/// A one-time move of selected records from one brain store (a Home) to another. <see cref="SourceHome"/> and <see cref="TargetHome"/>
/// are Home directories (the store is their <c>state</c> folder); both are opened with the same master key. <see cref="Backup"/>
/// holds the raw backup of every listed brain and the move ledger that makes each phase resumable.
/// </summary>
/// <param name="SourceHome">The Home the records are in now.</param>
/// <param name="TargetHome">The Home they move to.</param>
/// <param name="Backup">An empty or earlier backup directory of this move, outside both Homes.</param>
/// <param name="Brains">The brain ids whose stores are read; brain store folders are named by a hash, so the ids are listed.</param>
/// <param name="Rules">What moves.</param>
public sealed record BrainMovePlan(string SourceHome, string TargetHome, string Backup, string[] Brains, BrainRecordRule[] Rules);

/// <summary>The phases of a move, run in this order; each is idempotent and refuses to run before the one it depends on.</summary>
public enum BrainMovePhase
{
    /// <summary>Lists what the rules select in each brain. It writes nothing to the target or the backup, and creates no brain in the source.</summary>
    Plan,

    /// <summary>Copies every listed brain's store folder byte for byte into the backup and verifies the copy by SHA-256.</summary>
    Backup,

    /// <summary>After a verified backup: writes each selected record into the target, verifies it there, and records it in the ledger.</summary>
    Copy,

    /// <summary>After a verified copy: clears each copied record from the source, only while source and target still hold the copied bytes.</summary>
    RemoveSource,
}

/// <summary>One stored record as <see cref="BrainRecordMove.Read"/> finds it: its brain, grain id, state name and bytes.</summary>
public sealed record BrainStoredRecord(string Brain, string Grain, string State, byte[] Bytes);

/// <summary>What one phase did in one brain.</summary>
public sealed record BrainMoveLine(string Brain, int Selected, int Copied, int AlreadyThere, int Removed, string[] Conflicts);

/// <summary>
/// Moves selected records between two brain stores, for a store split (for example a service that leaves a shared cluster for its own).
/// Nothing is deleted from the source until the record's copy is verified in the target, and a record the target already holds with
/// other content is reported, never overwritten. Run it while no silo serves either store: it claims each brain's store as its writer.
/// </summary>
public static partial class BrainRecordMove
{
    private const string LedgerFile = "move-ledger.json";
    private const string BackupManifest = "backup-manifest.json";

    /// <summary>
    /// Runs <paramref name="phase"/> of <paramref name="plan"/>. <paramref name="custody"/> wraps the brain keys as the hosts of both
    /// stores do; without it, the runtime's default custody derives them from the master key <paramref name="configuration"/> carries
    /// under <paramref name="runtime"/>'s key. The Homes come from the plan.
    /// </summary>
    public static async Task<BrainMoveLine[]> Run(BrainMovePlan plan, BrainMovePhase phase, IConfiguration configuration, RuntimeOptions runtime, CancellationToken ct,
        IBrainKeyCustody? custody = null)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(runtime);
        Validate(plan);
        Directory.CreateDirectory(plan.Backup);
        var ledger = Ledger.Load(plan.Backup);
        return phase switch
        {
            BrainMovePhase.Plan => await WithStores(plan, configuration, runtime, custody, target: false, (source, _) => Select(plan, source, ct)).ConfigureAwait(false),
            BrainMovePhase.Backup => await BackupAsync(plan, configuration, runtime, custody, ledger, ct).ConfigureAwait(false),
            BrainMovePhase.Copy => await WithStores(plan, configuration, runtime, custody, target: true, (source, target) => CopyAsync(plan, source, target!, ledger, ct)).ConfigureAwait(false),
            BrainMovePhase.RemoveSource => await WithStores(plan, configuration, runtime, custody, target: true, (source, target) => RemoveAsync(plan, source, target!, ledger, ct)).ConfigureAwait(false),
            _ => throw new ArgumentOutOfRangeException(nameof(phase)),
        };
    }

    private static void Validate(BrainMovePlan plan)
    {
        if (string.IsNullOrWhiteSpace(plan.SourceHome) || string.IsNullOrWhiteSpace(plan.TargetHome) || string.IsNullOrWhiteSpace(plan.Backup))
        { throw new ArgumentException("A move names a source Home, a target Home and a backup directory."); }
        var source = Path.GetFullPath(plan.SourceHome);
        var target = Path.GetFullPath(plan.TargetHome);
        var backup = Path.GetFullPath(plan.Backup);
        if (Inside(source, target) || Inside(target, source)) { throw new ArgumentException("The source and target Homes must be separate folders."); }
        if (Inside(backup, source) || Inside(backup, target)) { throw new ArgumentException("The backup must be outside both Homes."); }
        if (plan.Brains is not { Length: > 0 } || plan.Rules is not { Length: > 0 }) { throw new ArgumentException("A move lists at least one brain and one rule."); }
        if (plan.Rules.Any(rule => string.IsNullOrWhiteSpace(rule.Neuron) && string.IsNullOrWhiteSpace(rule.Grain)))
        { throw new ArgumentException("Every rule names a neuron type or a grain prefix."); }
    }

    private static bool Inside(string path, string folder)
    {
        var root = folder.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return string.Equals(path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar), folder.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar), StringComparison.OrdinalIgnoreCase)
            || path.StartsWith(root, StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<BrainMoveLine[]> Select(BrainMovePlan plan, Store source, CancellationToken ct)
    {
        var lines = new List<BrainMoveLine>();
        foreach (var brain in plan.Brains)
        {
            ct.ThrowIfCancellationRequested();
            if (!source.Exists(brain)) { lines.Add(new BrainMoveLine(brain, 0, 0, 0, 0, [])); continue; }
            var log = await source.Logs.For(brain).ConfigureAwait(false);
            lines.Add(new BrainMoveLine(brain, Selected(plan, log).Count, 0, 0, 0, []));
        }
        return [.. lines];
    }

    private static List<(string Grain, string State, DigitalBrain.Kernel.JournalRecord Record)> Selected(BrainMovePlan plan, BrainLog log)
        => [.. log.Records().Where(record => record.Record.Bytes.Length > 0 && plan.Rules.Any(rule => rule.Matches(record.Grain, record.State)))
            .OrderBy(record => record.Grain, StringComparer.Ordinal).ThenBy(record => record.State, StringComparer.Ordinal)];

    // Raw copy of each brain's store folder, then a verify pass over the copy. A brain already backed up and still verifying is kept.
    private static async Task<BrainMoveLine[]> BackupAsync(BrainMovePlan plan, IConfiguration configuration, RuntimeOptions runtime, IBrainKeyCustody? custody, Ledger ledger,
        CancellationToken ct)
    {
        await using var source = Store.Open(plan.SourceHome, configuration, runtime, custody);
        var lines = new List<BrainMoveLine>();
        foreach (var brain in plan.Brains)
        {
            ct.ThrowIfCancellationRequested();
            var folder = source.FolderOf(brain);
            var into = Path.Combine(Path.GetFullPath(plan.Backup), "brains", Path.GetFileName(folder));
            if (ledger.BackedUp(brain) is { } kept && Verifies(into, kept)) { lines.Add(new BrainMoveLine(brain, 0, 0, kept.Length, 0, [])); continue; }
            if (!Directory.Exists(folder)) { ledger.Backup(brain, []); lines.Add(new BrainMoveLine(brain, 0, 0, 0, 0, [])); continue; }
            Directory.CreateDirectory(into);
            var files = new List<BackedUpFile>();
            foreach (var file in Directory.EnumerateFiles(folder).Order(StringComparer.Ordinal))
            {
                var name = Path.GetFileName(file);
                var copy = Path.Combine(into, name);
                // FileShare.ReadWrite: the copy reads beside a lease file a stopped host may have left; a running writer is the operator's to stop.
                await using (var input = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                await using (var output = new FileStream(copy, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    await input.CopyToAsync(output, ct).ConfigureAwait(false);
                    await output.FlushAsync(ct).ConfigureAwait(false);
                    output.Flush(flushToDisk: true);
                }
                files.Add(new BackedUpFile(name, Hash(file)));
            }
            if (!Verifies(into, [.. files])) { throw new IOException($"The backup of brain '{brain}' does not match its store; nothing was moved."); }
            ledger.Backup(brain, [.. files]);
            await ledger.Save(plan.Backup, ct).ConfigureAwait(false);
            lines.Add(new BrainMoveLine(brain, 0, files.Count, 0, 0, []));
        }
        await File.WriteAllTextAsync(Path.Combine(plan.Backup, BackupManifest), "verified " + DateTimeOffset.UtcNow.ToString("O"), ct).ConfigureAwait(false);
        await ledger.Save(plan.Backup, ct).ConfigureAwait(false);
        return [.. lines];
    }

    private static bool Verifies(string folder, BackedUpFile[] files)
        => files.All(file => File.Exists(Path.Combine(folder, file.Name)) && string.Equals(Hash(Path.Combine(folder, file.Name)), file.Sha256, StringComparison.Ordinal));

    private static async Task<BrainMoveLine[]> CopyAsync(BrainMovePlan plan, Store source, Store target, Ledger ledger, CancellationToken ct)
    {
        var lines = new List<BrainMoveLine>();
        foreach (var brain in plan.Brains)
        {
            ct.ThrowIfCancellationRequested();
            if (ledger.BackedUp(brain) is null) { throw new InvalidOperationException($"Brain '{brain}' has no verified backup; run the Backup phase first."); }
            if (!source.Exists(brain))
            {
                // Nothing to copy is a verified copy of nothing.
                ledger.Verified(brain, true);
                await ledger.Save(plan.Backup, ct).ConfigureAwait(false);
                lines.Add(new BrainMoveLine(brain, 0, 0, 0, 0, []));
                continue;
            }
            var from = await source.Logs.For(brain).ConfigureAwait(false);
            var selected = Selected(plan, from);
            var to = await target.Logs.For(brain).ConfigureAwait(false);
            var copied = 0;
            var there = 0;
            var conflicts = new List<string>();
            foreach (var (grain, state, record) in selected)
            {
                var bytes = record.Bytes.ToArray();
                var existing = to.Record(grain, state);
                if (existing is not null && existing.Bytes.Span.SequenceEqual(bytes) && existing.Type == record.Type)
                {
                    there++;
                    ledger.Copied(brain, grain, state, Hash(bytes));
                    continue;
                }
                if (existing is not null)
                {
                    // The target already serves other content for this record: it is the newer owner's, never overwritten.
                    conflicts.Add(grain + " " + state);
                    continue;
                }
                await to.AppendEncoded(grain, state, null, BrainRecordChange.Write, bytes, record.Type, "", [], null, ct).ConfigureAwait(false);
                if (to.Record(grain, state) is not { } written || !written.Bytes.Span.SequenceEqual(bytes))
                { throw new IOException($"The copy of {grain} {state} in brain '{brain}' did not verify; the source is untouched."); }
                ledger.Copied(brain, grain, state, Hash(bytes));
                copied++;
            }
            ledger.Verified(brain, conflicts.Count == 0);
            await ledger.Save(plan.Backup, ct).ConfigureAwait(false);
            lines.Add(new BrainMoveLine(brain, selected.Count, copied, there, 0, [.. conflicts]));
        }
        return [.. lines];
    }

    private static async Task<BrainMoveLine[]> RemoveAsync(BrainMovePlan plan, Store source, Store target, Ledger ledger, CancellationToken ct)
    {
        var lines = new List<BrainMoveLine>();
        foreach (var brain in plan.Brains)
        {
            ct.ThrowIfCancellationRequested();
            if (!ledger.IsVerified(brain)) { throw new InvalidOperationException($"Brain '{brain}' has no verified copy without conflicts; nothing is removed."); }
            if (!source.Exists(brain)) { lines.Add(new BrainMoveLine(brain, 0, 0, 0, 0, [])); continue; }
            var from = await source.Logs.For(brain).ConfigureAwait(false);
            var to = await target.Logs.For(brain).ConfigureAwait(false);
            var removed = 0;
            var kept = new List<string>();
            foreach (var entry in ledger.CopiesOf(brain))
            {
                var inSource = from.Record(entry.Grain, entry.State);
                if (inSource is null) { continue; }
                var inTarget = to.Record(entry.Grain, entry.State);
                // Only while both still hold exactly what was copied: a record that changed on either side since the copy stays.
                if (inTarget is null || Hash(inTarget.Bytes.ToArray()) != entry.Sha256 || Hash(inSource.Bytes.ToArray()) != entry.Sha256)
                {
                    kept.Add(entry.Grain + " " + entry.State);
                    continue;
                }
                await from.AppendEncoded(entry.Grain, entry.State, inSource.Version, BrainRecordChange.Clear, [], null, "", [], null, ct).ConfigureAwait(false);
                removed++;
            }
            lines.Add(new BrainMoveLine(brain, 0, 0, 0, removed, [.. kept]));
        }
        return [.. lines];
    }

    /// <summary>
    /// The records <paramref name="rule"/> selects in each listed brain of the store under <paramref name="home"/>, for an operator's
    /// inventory before a move. It changes no record and creates no brain; like every phase it claims each store as its writer, so it
    /// runs while no host serves the store.
    /// </summary>
    public static async Task<BrainStoredRecord[]> Read(string home, IEnumerable<string> brains, BrainRecordRule rule, IConfiguration configuration,
        RuntimeOptions runtime, CancellationToken ct, IBrainKeyCustody? custody = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(home);
        ArgumentNullException.ThrowIfNull(brains);
        ArgumentNullException.ThrowIfNull(rule);
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(runtime);
        await using var store = Store.Open(home, configuration, runtime, custody);
        var found = new List<BrainStoredRecord>();
        foreach (var brain in brains.Distinct(StringComparer.Ordinal))
        {
            ct.ThrowIfCancellationRequested();
            if (!store.Exists(brain)) { continue; }
            var log = await store.Logs.For(brain).ConfigureAwait(false);
            found.AddRange(log.Records()
                .Where(record => record.Record.Bytes.Length > 0 && rule.Matches(record.Grain, record.State))
                .OrderBy(record => record.Grain, StringComparer.Ordinal).ThenBy(record => record.State, StringComparer.Ordinal)
                .Select(record => new BrainStoredRecord(brain, record.Grain, record.State, record.Record.Bytes.ToArray())));
        }
        return [.. found];
    }

    private static async Task<BrainMoveLine[]> WithStores(BrainMovePlan plan, IConfiguration configuration, RuntimeOptions runtime, IBrainKeyCustody? custody, bool target,
        Func<Store, Store?, Task<BrainMoveLine[]>> run)
    {
        await using var source = Store.Open(plan.SourceHome, configuration, runtime, custody);
        await using var into = target ? Store.Open(plan.TargetHome, configuration, runtime, custody) : null;
        return await run(source, into).ConfigureAwait(false);
    }

    private static string Hash(string file)
    {
        using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        return Convert.ToHexStringLower(SHA256.HashData(stream));
    }

    private static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    // One Home's brain store, opened outside a silo with the same services a silo uses.
    private sealed class Store(ServiceProvider services) : IAsyncDisposable
    {
        public BrainLogs Logs { get; } = services.GetRequiredService<BrainLogs>();

        public static Store Open(string home, IConfiguration configuration, RuntimeOptions runtime, IBrainKeyCustody? custody)
        {
            var services = new ServiceCollection();
            services.AddSingleton(runtime);
            if (custody is not null) { services.AddSingleton(custody); }
            services.AddSingleton<IConfiguration>(new ConfigurationBuilder()
                .AddConfiguration(configuration)
                .AddInMemoryCollection(new Dictionary<string, string?> { [runtime.HomeKey] = Path.GetFullPath(home) })
                .Build());
            services.AddSerializer(serializer => serializer.AddAssembly(typeof(BrainLog).Assembly));
            BrainStorageHosting.AddBrainStorageServices(services, []);
            return new Store(services.BuildServiceProvider());
        }

        public string FolderOf(string brain) => services.GetRequiredService<BrainStorageKeys>().DirectoryOf(brain);

        public bool Exists(string brain)
            => Directory.Exists(FolderOf(brain)) && File.Exists(Path.Combine(FolderOf(brain), "brain.key"));

        public async ValueTask DisposeAsync()
        {
            await Logs.DisposeAsync().ConfigureAwait(false);
            await services.DisposeAsync().ConfigureAwait(false);
        }
    }

    // The move's memory between phases and runs, kept in the backup directory.
    private sealed class Ledger
    {
        public Dictionary<string, BrainLedger> Brains { get; set; } = new(StringComparer.Ordinal);

        public static Ledger Load(string backup)
        {
            var path = Path.Combine(backup, LedgerFile);
            return File.Exists(path)
                ? JsonSerializer.Deserialize(File.ReadAllBytes(path), BrainMoveJson.Default.Ledger) ?? new Ledger()
                : new Ledger();
        }

        public async Task Save(string backup, CancellationToken ct)
        {
            var path = Path.Combine(backup, LedgerFile);
            var pending = path + ".tmp";
            await File.WriteAllBytesAsync(pending, JsonSerializer.SerializeToUtf8Bytes(this, BrainMoveJson.Default.Ledger), ct).ConfigureAwait(false);
            File.Move(pending, path, overwrite: true);
        }

        public BackedUpFile[]? BackedUp(string brain) => Brains.TryGetValue(brain, out var entry) ? entry.Backup : null;

        public void Backup(string brain, BackedUpFile[] files) => Of(brain).Backup = files;

        public void Copied(string brain, string grain, string state, string sha256)
        {
            var entry = Of(brain);
            entry.Copies = [.. entry.Copies.Where(copy => copy.Grain != grain || copy.State != state), new CopiedRecord(grain, state, sha256)];
        }

        public void Verified(string brain, bool verified) => Of(brain).Verified = verified;

        public bool IsVerified(string brain) => Brains.TryGetValue(brain, out var entry) && entry.Verified;

        public CopiedRecord[] CopiesOf(string brain) => Brains.TryGetValue(brain, out var entry) ? entry.Copies : [];

        private BrainLedger Of(string brain)
        {
            if (!Brains.TryGetValue(brain, out var entry)) { Brains[brain] = entry = new BrainLedger(); }
            return entry;
        }
    }

    private sealed class BrainLedger
    {
        public BackedUpFile[]? Backup { get; set; }
        public CopiedRecord[] Copies { get; set; } = [];
        public bool Verified { get; set; }
    }

    private sealed record BackedUpFile(string Name, string Sha256);

    private sealed record CopiedRecord(string Grain, string State, string Sha256);

    [JsonSourceGenerationOptions(JsonSerializerDefaults.Web, WriteIndented = true)]
    [JsonSerializable(typeof(Ledger))]
    private sealed partial class BrainMoveJson : JsonSerializerContext;
}
