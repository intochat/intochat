using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using DigitalBrain.Authority.Keys;
using DigitalBrain.Authority.Storage;
using DigitalBrain.Framework;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Orleans.Metadata;
using Orleans.Runtime;
using Orleans.Serialization;
using Orleans.Storage;

namespace DigitalBrain.OS.Testing.Storage;

// Storage fixtures (clean-core W1, section 4.4): brain logs and grain-state payloads written by a reproducible build,
// sealed under the checked-in TestSealKey, so every later build proves it still reads them. A fixture directory holds
// It lives in this unpublished test-support project because it opens Authority internals (the brain log and its keys).
// fixture.json, brain-store/ (the brain directories as the host wrote them, minus their writer locks) and grain-state/.
public static class StorageFixtures
{
    public const string ManifestFile = "fixture.json";
    public const string BrainStore = "brain-store";
    public const string GrainState = "grain-state";

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    // A fixture host's brain storage: the test seal key as its master key (custody roots on its UTF-8 bytes, as
    // PlatformHosting reads the configured key), the fixture's directory and the segment format it writes.
    public static TestBrain WithFixtureStorage(this TestBrain brain, string brainStore, BrainLogFormat format)
    {
        ArgumentNullException.ThrowIfNull(brain);
        return brain
            .WithConfiguration(DigitalBrainNames.MasterKeyConfigurationKey, TestSealKey.Base64)
            .WithConfiguration(BrainStoreOptions.Key + ":" + nameof(BrainStoreOptions.Directory), brainStore)
            .WithConfiguration(BrainStoreOptions.Key + ":" + nameof(BrainStoreOptions.WriteFormat), format.ToString());
    }

    // Writes the brain's state into a snapshot and starts a fresh segment in the host's write format.
    public static async Task CheckpointAsync(IServiceProvider services, string brainId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(services);
        var log = await services.GetRequiredService<BrainLogs>().For(brainId).WaitAsync(ct).ConfigureAwait(false);
        await log.Checkpoint(ct).ConfigureAwait(false);
    }

    // The directory a brain's log lives in under a brain store.
    public static string DirectoryOf(string brainStore, string brainId) => Keys(brainStore).DirectoryOf(brainId);

    // The platform grain state a host keeps in memory, re-encoded the way Azure blob grain storage writes it.
    public static IEnumerable<(string Store, string GrainId, string StateName, byte[] Payload)> GrainStatePayloads(IServiceProvider services)
    {
        ArgumentNullException.ThrowIfNull(services);
        var types = new StateTypes(services);
        var objects = services.GetRequiredService<ObjectSerializer>();
        var blob = new OrleansGrainStorageSerializer(services.GetRequiredService<Serializer>());
        var stores = services.GetRequiredService<DigitalBrain.Microsoft.Orleans.MemoryStores>();
        foreach (var store in new[] { DigitalBrainNames.ClusterGrainStorage, DigitalBrainNames.PlatformGrainStorage })
        {
            foreach (var (key, (state, _)) in stores.Store(store).OrderBy(entry => entry.Key, StringComparer.Ordinal))
            {
                var split = key.LastIndexOf('|');
                var (grainId, stateName) = (key[..split], key[(split + 1)..]);
                if (types.Find(grainId, stateName) is not { } type) { continue; }
                var value = objects.Deserialize(state, type);
                yield return (store, grainId, stateName, Serialize(blob, type, value).ToArray());
            }
        }
    }

    public static void WriteManifest(string directory, StorageFixtureManifest manifest)
        => File.WriteAllText(Path.Combine(directory, ManifestFile), JsonSerializer.Serialize(manifest, Json).ReplaceLineEndings("\n") + "\n");

    public static StorageFixtureManifest ReadManifest(string directory)
        => JsonSerializer.Deserialize<StorageFixtureManifest>(File.ReadAllText(Path.Combine(directory, ManifestFile)), Json)!;

    // Opens one fixture brain the way the host does (its wrapped key under the test seal key, then the log) and reads
    // every record with its CLR type and every journalled signal. Never opens the checked-in files: copy them first,
    // because opening a log claims its writer lock and may truncate a torn tail.
    public static async Task<StorageFixtureRead> ReadBrainAsync(IServiceProvider services, string brainStore, string brainId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(services);
        var keys = Keys(brainStore);
        var directory = keys.DirectoryOf(brainId);
        // GetOrCreate would mint a key for a missing brain; a fixture brain always carries its own.
        if (!File.Exists(Path.Combine(directory, "brain.key"))) { throw new FileNotFoundException($"Fixture brain {brainId} has no brain.key.", directory); }
        var key = await keys.GetOrCreate(brainId, ct).ConfigureAwait(false);
        var types = new StateTypes(services);
        var objects = services.GetRequiredService<ObjectSerializer>();
        using var log = new BrainLog(directory, brainId, key, services.GetRequiredService<Serializer>());
        var records = new List<StorageFixtureRecord>();
        var unreadable = new List<string>();
        foreach (var (grainId, stateName) in log.RecordKeys().OrderBy(pair => pair.GrainId, StringComparer.Ordinal).ThenBy(pair => pair.StateName, StringComparer.Ordinal))
        {
            var record = log.Read(grainId, stateName)!;
            if (types.Find(grainId, stateName) is not { } type) { unreadable.Add($"{grainId} {stateName}: no state type"); continue; }
            try
            {
                var value = objects.Deserialize(record.Record, type);
                if (value is null || !type.IsInstanceOfType(value)) { unreadable.Add($"{grainId} {stateName}: read {value?.GetType().FullName ?? "null"}, not {type.FullName}"); }
                else { records.Add(new(grainId, stateName, type)); }
            }
            catch (Exception error) when (error is not OperationCanceledException)
            { unreadable.Add($"{grainId} {stateName}: {type.FullName}: {error.GetType().Name}: {error.Message}"); }
        }
        var signals = await log.ReadSignals(0, int.MaxValue, null, ct).ConfigureAwait(false);
        return new(brainId, log.Format, File.Exists(Path.Combine(directory, "current")), [.. records], [.. unreadable], [.. signals.Select(signal => signal.Signal)]);
    }

    // Reads one grain-state payload with its CLR type through the blob storage serializer.
    public static object? ReadGrainState(IServiceProvider services, StorageFixtureBlob blob, byte[] payload)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(blob);
        var type = new StateTypes(services).Find(blob.GrainId, blob.StateName)
            ?? throw new InvalidOperationException($"{blob.GrainId} {blob.StateName} has no state type.");
        var serializer = new OrleansGrainStorageSerializer(services.GetRequiredService<Serializer>());
        return typeof(IGrainStorageSerializer).GetMethod(nameof(IGrainStorageSerializer.Deserialize))!.MakeGenericMethod(type)
            .Invoke(serializer, [new BinaryData(payload)]);
    }

    private static BinaryData Serialize(IGrainStorageSerializer serializer, Type type, object? value)
        => (BinaryData)typeof(IGrainStorageSerializer).GetMethod(nameof(IGrainStorageSerializer.Serialize))!.MakeGenericMethod(type).Invoke(serializer, [value])!;

    private static BrainStorageKeys Keys(string brainStore)
        => new(new ConfiguredMasterKeyCustody(Encoding.UTF8.GetBytes(TestSealKey.Base64)), Options.Create(new BrainStoreOptions { Directory = brainStore }));

    // (grain type, state name) -> the state's CLR type, from every loaded grain class's [PersistentState] parameters and
    // the grain type the silo resolves for it (implicit names included).
    private sealed class StateTypes
    {
        private readonly Dictionary<(string Grain, string State), Type> _types = [];

        public StateTypes(IServiceProvider services)
        {
            var resolver = services.GetRequiredService<GrainTypeResolver>();
            foreach (var type in AppDomain.CurrentDomain.GetAssemblies().Where(assembly => !assembly.IsDynamic).SelectMany(Types))
            {
                if (type.IsAbstract || type.ContainsGenericParameters || !typeof(IGrain).IsAssignableFrom(type)) { continue; }
                foreach (var parameter in type.GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance).SelectMany(constructor => constructor.GetParameters()))
                {
                    if (parameter.GetCustomAttribute<PersistentStateAttribute>() is not { } state || !parameter.ParameterType.IsGenericType
                        || parameter.ParameterType.GetGenericTypeDefinition() != typeof(IPersistentState<>)) { continue; }
                    _types.TryAdd((resolver.GetGrainType(type).ToString()!, state.StateName), parameter.ParameterType.GetGenericArguments()[0]);
                }
            }
        }

        public Type? Find(string grainId, string stateName)
            => GrainId.TryParse(grainId, out var id) && _types.TryGetValue((id.Type.ToString()!, stateName), out var type) ? type : null;

        private static Type[] Types(Assembly assembly)
        {
            try { return assembly.GetTypes(); }
            catch (ReflectionTypeLoadException error) { return [.. error.Types.OfType<Type>()]; }
        }
    }
}

public sealed record StorageFixtureManifest(string Source, string CapturedFrom, string SealKey, StorageFixtureBrain[] Brains, StorageFixtureBlob[] GrainState, string[] Scenarios);

public sealed record StorageFixtureBrain(string BrainId, BrainLogFormat Format, bool Snapshot, int Records, int Signals, string[] SignalTypes);

public sealed record StorageFixtureBlob(string File, string Store, string GrainId, string StateName);

public sealed record StorageFixtureRecord(string GrainId, string StateName, Type Type);

public sealed record StorageFixtureRead(string BrainId, BrainLogFormat Format, bool Snapshot, StorageFixtureRecord[] Records, string[] Unreadable, Signal[] Signals);
