using System.Reflection;
using DigitalBrain.Access;
using DigitalBrain.Contracts;
using DigitalBrain.Neurons;
using DigitalBrain.Units;

namespace DigitalBrain.Testing;

/// <summary>
/// An <see cref="IAppSessionDriver"/> that drives an <see cref="IScreen"/> neuron directly against any
/// <see cref="INeuronRuntime"/> (in-memory <see cref="TestOS"/> or Orleans silo) or <see cref="TestOS"/>.
/// </summary>
public sealed class DirectNeuronSessionDriver : IAppSessionDriver
{
    private readonly IScreen _screen;
    private readonly Func<Task> _idle;
    private readonly EdgeAuthority _edge;
    private readonly Caller _person;
    private PublishedView? _lastView;
    private bool _disposed;

    /// <summary>Creates a driver targeting <paramref name="screenType"/> on <paramref name="runtime"/>.</summary>
    /// <param name="runtime">The neuron runtime.</param>
    /// <param name="screenType">The concrete screen type.</param>
    /// <param name="key">Optional screen neuron key; when omitted, an isolated unique key is generated.</param>
    public DirectNeuronSessionDriver(INeuronRuntime runtime, Type screenType, string? key = null)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        ArgumentNullException.ThrowIfNull(screenType);

        var alias = ResolveScreenAlias(screenType);
        var screenKey = string.IsNullOrWhiteSpace(key) ? $"session-{Guid.NewGuid():N}" : key;
        var screenId = runtime.Id(alias, screenKey);

        _screen = runtime.Get<IScreen>(screenId);
        _idle = runtime.Idle;
        _edge = runtime.Edge;
        _person = _edge.Person("app-session", "app-session-account", runtime.Brain);
    }

    /// <summary>Creates a driver targeting <paramref name="screenType"/> on <paramref name="os"/>.</summary>
    /// <param name="os">The in-memory <see cref="TestOS"/>.</param>
    /// <param name="screenType">The concrete screen type.</param>
    /// <param name="key">Optional screen neuron key; when omitted, an isolated unique key is generated.</param>
    /// <param name="brain">Optional brain id; defaults to <c>"test"</c>.</param>
    public DirectNeuronSessionDriver(TestOS os, Type screenType, string? key = null, BrainId? brain = null)
    {
        ArgumentNullException.ThrowIfNull(os);
        ArgumentNullException.ThrowIfNull(screenType);

        var brainId = brain ?? new BrainId("test");
        var alias = ResolveScreenAlias(screenType);
        var screenKey = string.IsNullOrWhiteSpace(key) ? $"session-{Guid.NewGuid():N}" : key;
        var screenId = new NeuronId(brainId, alias, screenKey);

        _screen = os.Get<IScreen>(screenId);
        _idle = () => os.Idle();
        _edge = os.Edge;
        _person = _edge.Person("app-session", "app-session-account", brainId);
    }

    /// <inheritdoc />
    public async Task<PublishedView> ReadViewAsync(CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ct.ThrowIfCancellationRequested();

        using var scope = _edge.Enter(_person);
        var view = await _screen.Read().ConfigureAwait(false)
            ?? throw new InvalidOperationException("The screen returned no published view.");
        _lastView = view;
        return view;
    }

    /// <inheritdoc />
    public async Task<PublishedView> ActivateAsync(string key, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ct.ThrowIfCancellationRequested();

        var current = _lastView ?? await ReadViewAsync(ct).ConfigureAwait(false);
        using (var scope = _edge.Enter(_person))
        {
            await _screen.Activate(current.Alias, current.Generation, key).ConfigureAwait(false);
        }
        await WaitForIdleAsync(ct).ConfigureAwait(false);
        return await ReadViewAsync(ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<PublishedView> SendAsync<TSignal>(TSignal signal, CancellationToken ct = default) where TSignal : Signal
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(signal);
        ct.ThrowIfCancellationRequested();

        using (var scope = _edge.Enter(_person))
        {
            await _screen.Request(signal).ConfigureAwait(false);
        }
        await WaitForIdleAsync(ct).ConfigureAwait(false);
        return await ReadViewAsync(ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task WaitForIdleAsync(CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ct.ThrowIfCancellationRequested();
        await _idle().ConfigureAwait(false);
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        _disposed = true;
        return ValueTask.CompletedTask;
    }

    private static string ResolveScreenAlias(Type screenType)
        => PlainNeuronContracts.TypeOf(screenType)
            ?? screenType.GetCustomAttribute<NeuronAttribute>()?.Type
            ?? screenType.Name;
}
