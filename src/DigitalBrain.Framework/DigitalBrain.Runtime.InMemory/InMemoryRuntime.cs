using System.Collections.Concurrent;
using System.Reflection;
using DigitalBrain.Framework;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Orleans.Metadata;
using Orleans.Serialization;

namespace DigitalBrain.Runtime.InMemory;

// The in-memory runtime of the clean-core plan (2.5): one host per address with a turn lock, calls through
// DispatchProxy straight to the host, state through the same codec as Orleans, timers on the injected
// TimeProvider. Out of scope: placement, several processes, durability.
public sealed class InMemoryRuntime : INeuronResolver, IAsyncDisposable
{
    private readonly ConcurrentDictionary<NeuronAddress, NeuronHost> _hosts = new();
    private readonly ConcurrentDictionary<Type, string> _neuronTypes = new();
    private readonly Dictionary<string, Type> _classes;
    private readonly ServiceProvider _services;

    private InMemoryRuntime(TimeProvider time, Dictionary<string, Type> classes, IServiceCollection services)
    {
        Time = time;
        _classes = classes;
        services.TryAddSingleton<INeuronResolver>(this);
        _services = services.BuildServiceProvider();
        State = new MemoryStateStore();
        Codec = _services.GetRequiredService<IStateCodec>();
    }

    // Every call waits at most this long for its answer unless its method declares [CallTimeout] or [ResponseTimeout].
    public static readonly TimeSpan DefaultCallTimeout = TimeSpan.FromSeconds(30);

    public TimeProvider Time { get; }

    public IServiceProvider Services => _services;

    internal MemoryStateStore State { get; }

    internal IStateCodec Codec { get; }

    public static InMemoryRuntime Create(TimeProvider time, Action<IRuntimeBuilder> configure)
    {
        ArgumentNullException.ThrowIfNull(time);
        ArgumentNullException.ThrowIfNull(configure);
        var builder = new Builder(time);
        configure(builder);
        return new InMemoryRuntime(time, builder.Classes, builder.Services);
    }

    public T Resolve<T>(BrainId brain, NeuronId relative) where T : class
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(brain.Value);
        return ResolveKey<T>(relative.Within(brain.Value).Name);
    }

    public T ResolveKey<T>(string fullKey) where T : class
    {
        ArgumentException.ThrowIfNullOrEmpty(fullKey);
        return Reference<T>(new NeuronAddress(NeuronTypeOf(typeof(T)), fullKey));
    }

    // A reference to the neuron at an exact address.
    public T Reference<T>(NeuronAddress address) where T : class => NeuronProxy.Create<T>(Host(address));

    // Waits until every queued turn, timer tick and one-way call has run, so a fact can assert after Advance.
    public async Task Idle()
    {
        while (_hosts.Values.Select(host => host.Idle()).Where(task => !task.IsCompleted).ToArray() is { Length: > 0 } busy)
        { await Task.WhenAll(busy); }
    }

    // Completes once a call is waiting for the turn of the neuron at this key: a fact's synchronisation point before
    // it moves the clock, since the waiting call's response timeout is armed by then.
    public Task Waiting<T>(string fullKey) where T : class => Host(new NeuronAddress(NeuronTypeOf(typeof(T)), fullKey)).Waiting();

    public async ValueTask DisposeAsync()
    {
        foreach (var host in _hosts.Values) { await host.DisposeAsync(); }
        await _services.DisposeAsync();
    }

    internal NeuronHost Host(NeuronAddress address) => _hosts.GetOrAdd(address, a => new NeuronHost(this, a, ClassOf(a.NeuronType)));

    private Type ClassOf(string neuronType) => _classes.TryGetValue(neuronType, out var type)
        ? type
        : throw new InvalidOperationException($"No neuron class is registered for neuron type '{neuronType}'.");

    // The neuron type a contract resolves to: its default type, or the one registered class that implements it.
    private string NeuronTypeOf(Type contract) => _neuronTypes.GetOrAdd(contract, c =>
    {
        if (c.GetCustomAttribute<DefaultNeuronTypeAttribute>(false)?.Type is { } neutral) { return neutral; }
        if (c.GetCustomAttributesData().FirstOrDefault(a => a.AttributeType == typeof(DefaultGrainTypeAttribute)) is { } grain)
        { return (string)grain.ConstructorArguments[0].Value!; }
        var implementers = _classes.Where(entry => c.IsAssignableFrom(entry.Value)).Select(entry => entry.Key).ToArray();
        return implementers.Length == 1
            ? implementers[0]
            : throw new InvalidOperationException($"{c.FullName} is implemented by {implementers.Length} registered neuron classes; register one or give the contract a default type.");
    });

    // [GrainType] when present, otherwise Orleans' default: the class name in lower case, without a "Grain" suffix.
    internal static string NeuronTypeOfClass(Type type)
    {
        if (type.GetCustomAttribute<GrainTypeAttribute>(false) is { } attribute) { return attribute.GetGrainType(null!, type).ToString()!; }
        var name = type.Name;
        if (name.EndsWith("Grain", StringComparison.Ordinal) && name.Length > 5) { name = name[..^5]; }
        return name.ToLowerInvariant();
    }

    private sealed class Builder : IRuntimeBuilder
    {
        public Builder(TimeProvider time)
        {
            Services.AddSingleton(time);
            Services.AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance);
            Services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
            Services.AddOptions();
            Services.AddSingleton<ISignalJournal, MemorySignalJournal>();
            Services.AddSingleton<LocalSignalHub>();
            Services.AddSingleton<ILocalSignalHub>(provider => provider.GetRequiredService<LocalSignalHub>());
            Services.AddSerializer();
            Services.TryAddSingleton<IStateCodec, OrleansStateCodec>();
            Services.AddSingleton(Configuration);
        }

        public Dictionary<string, Type> Classes { get; } = new(StringComparer.Ordinal);

        public IServiceCollection Services { get; } = new ServiceCollection();

        public IConfiguration Configuration { get; } = new ConfigurationBuilder().Build();

        public IRuntimeBuilder AddNeuron<TImplementation>() where TImplementation : class
        {
            var type = typeof(TImplementation);
            if (!typeof(Neuron).IsAssignableFrom(type) || type.IsAbstract)
            { throw new ArgumentException($"{type.FullName} is not a concrete neuron class.", nameof(TImplementation)); }
            // Orleans' predicate takes an IInvokable this runtime does not build; refusing it keeps the two runtimes from
            // scheduling the same class differently. Neutral classes use [InterleavesWhen].
            if (type.IsDefined(typeof(Orleans.Concurrency.MayInterleaveAttribute), false))
            { throw new NotSupportedException($"{type.FullName} declares Orleans [MayInterleave]; declare [InterleavesWhen] to run it in memory."); }
            var neuronType = NeuronTypeOfClass(type);
            if (!Classes.TryAdd(neuronType, type) && Classes[neuronType] != type)
            { throw new InvalidOperationException($"Neuron type '{neuronType}' is registered for {Classes[neuronType].FullName} and {type.FullName}."); }
            return this;
        }

        public IRuntimeBuilder AddStateStore(string name, Func<IServiceProvider, INeuronStateStore> store) =>
            throw new NotSupportedException("The in-memory runtime keeps every state in its own store.");

        public bool TryGetProvider<TBuilder>(out TBuilder? builder) where TBuilder : class
        {
            builder = null;
            return false;
        }
    }

    // The Orleans binary codec, so a state type the silo cannot serialize fails in a unit test too.
    private sealed class OrleansStateCodec(Serializer serializer) : IStateCodec
    {
        public byte CodecId => 1;

        public byte[] Serialize<T>(T value) => serializer.SerializeToArray(value);

        public T Deserialize<T>(ReadOnlySpan<byte> bytes) => serializer.Deserialize<T>(bytes.ToArray())!;
    }
}
