using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using IntoChat.OS.Authority.Storage;
using IntoChat.OS.Contracts.Configuration;
using IntoChat.OS.Sdk;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Metadata;
using Orleans.Runtime;
using Orleans.Serialization;
using Orleans.Storage;

namespace IntoChat.OS.Kernel.Testing.Storage;

// Storage fixtures (clean-core W1, section 4.4): brain logs and grain-state payloads written by a reproducible build,
// sealed under the checked-in TestSealKey, so every later build proves it still reads them. A fixture directory holds
// It lives in this unpublished test-support project and reads stores through Authority's public BrainStoreLayout.
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
    // PlatformHosting reads the configured key), the fixture's Home (its brain log is <home>/state) and the segment format it writes.
    public static TestBrain WithFixtureStorage(this TestBrain brain, string home, BrainLogFormat format)
    {
        ArgumentNullException.ThrowIfNull(brain);
        return brain
            .WithConfiguration(IntoChatOSNames.MasterKeyConfigurationKey, TestSealKey.Base64)
            .WithConfiguration(IntoChatOSNames.HomeKey, home)
            .WithConfiguration(BrainStoreOptions.Key + ":" + nameof(BrainStoreOptions.WriteFormat), format.ToString());
    }

    // Writes the brain's state into a snapshot and starts a fresh segment in the host's write format.
    public static Task CheckpointAsync(IServiceProvider services, string brainId, CancellationToken ct)
        => BrainStoreLayout.CheckpointAsync(services, brainId, ct);

    // The directory a brain's log lives in under a brain store.
    public static string DirectoryOf(string brainStore, string brainId) => BrainStoreLayout.DirectoryOf(brainStore, brainId);

    // The platform grain state a host keeps in memory, re-encoded the way Azure blob grain storage writes it.
    public static IEnumerable<(string Store, string GrainId, string StateName, byte[] Payload)> GrainStatePayloads(IServiceProvider services)
    {
        ArgumentNullException.ThrowIfNull(services);
        var types = new StateTypes(services);
        var objects = services.GetRequiredService<ObjectSerializer>();
        var blob = new OrleansGrainStorageSerializer(services.GetRequiredService<Serializer>());
        var stores = services.GetRequiredService<IntoChat.OS.Modules.Microsoft.Orleans.MemoryStores>();
        foreach (var store in new[] { IntoChatOSNames.ClusterGrainStorage, IntoChatOSNames.PlatformGrainStorage })
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
        // The layout reader never mints a key: a fixture brain always carries its own.
        var read = await BrainStoreLayout.ReadAsync(Encoding.UTF8.GetBytes(TestSealKey.Base64), brainStore, brainId, services.GetRequiredService<Serializer>(), ct)
            .ConfigureAwait(false);
        var types = new StateTypes(services);
        var objects = services.GetRequiredService<ObjectSerializer>();
        var records = new List<StorageFixtureRecord>();
        var unreadable = new List<string>();
        foreach (var (grainId, stateName, bytes) in read.Records)
        {
            if (types.Find(grainId, stateName) is not { } type) { unreadable.Add($"{grainId} {stateName}: no state type"); continue; }
            try
            {
                var value = objects.Deserialize(bytes, type);
                if (value is null || !type.IsInstanceOfType(value)) { unreadable.Add($"{grainId} {stateName}: read {value?.GetType().FullName ?? "null"}, not {type.FullName}"); }
                else { records.Add(new(grainId, stateName, type)); }
            }
            catch (Exception error) when (error is not OperationCanceledException)
            { unreadable.Add($"{grainId} {stateName}: {type.FullName}: {error.GetType().Name}: {error.Message}"); }
        }
        return new(brainId, read.Format, read.HasSnapshot, [.. records], [.. unreadable], [.. read.Signals]);
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
