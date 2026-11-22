using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;

namespace DigitalBrain.Testing;

/// <summary>
/// A journey test harness hosting modules and shared edge fakes on either <see cref="NeuronRuntimeKind.TestOS"/>
/// or <see cref="NeuronRuntimeKind.Silo"/>, with named people owning separate brains.
/// </summary>
public sealed class JourneyOS : IAsyncDisposable
{
    private readonly JourneyFixture _fixture;
    private readonly FakeSet _fakes;
    private readonly ConcurrentDictionary<string, Person> _people = new(StringComparer.Ordinal);

    private JourneyOS(JourneyFixture fixture, INeuronRuntime runtime, FakeSet fakes)
    {
        _fixture = fixture;
        Runtime = runtime;
        _fakes = fakes;
    }

    /// <summary>Starts a new <see cref="JourneyOS"/> on <paramref name="kind"/> with <paramref name="modules"/> and optional <paramref name="fakes"/>.</summary>
    public static async Task<JourneyOS> Start(
        NeuronRuntimeKind kind,
        IReadOnlyList<Type> modules,
        Action<FakeSet>? fakes = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(modules);
        var fakeSet = new FakeSet();
        fakes?.Invoke(fakeSet);

        var fixture = new JourneyFixture(modules, fakeSet);
        try
        {
            var runtime = await fixture.Start(kind, ct).ConfigureAwait(false);
            return new JourneyOS(fixture, runtime, fakeSet);
        }
        catch
        {
            await fixture.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>The underlying neuron runtime.</summary>
    public INeuronRuntime Runtime { get; }

    /// <summary>Returns the registered fake of type <typeparamref name="TFake"/>.</summary>
    /// <exception cref="InvalidOperationException">No fake of type <typeparamref name="TFake"/> was added.</exception>
    public TFake Fake<TFake>() where TFake : class => _fakes.Get<TFake>();

    /// <summary>Returns the named person (idempotent; account and brain both equal <paramref name="name"/>).</summary>
    public Person Person(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        return _people.GetOrAdd(name, n => new Person(Runtime, n));
    }

    /// <summary>Lets queued turns, deliveries and ticks finish.</summary>
    public Task Idle() => Runtime.Idle();

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        await Runtime.DisposeAsync().ConfigureAwait(false);
        await _fixture.DisposeAsync().ConfigureAwait(false);
    }

    private sealed class JourneyFixture(IReadOnlyList<Type> modules, FakeSet fakes) : BothRuntimesFixture
    {
        protected override IReadOnlyList<Type> Modules { get; } = modules;

        protected override void ConfigureTestOS(IServiceCollection services) => fakes.Apply(services);

        protected override void ConfigureSilo(IServiceCollection services) => fakes.Apply(services);
    }
}
