using DigitalBrain.Access;

namespace DigitalBrain.Testing.Conformance;

/// <summary>
/// Switches the caller the subject sees. secrets@1 and identity@1 take no caller parameter: the implementation reads the
/// caller the runtime stamped on the call. A kit subclass implements <see cref="As"/> by stamping <paramref name="caller"/>
/// the way its runtime does, until the returned scope is disposed.
/// </summary>
public abstract class CallerScope
{
    /// <summary>Makes subsequent calls on this async flow run as <paramref name="caller"/> until the result is disposed.</summary>
    public abstract IDisposable As(Caller caller);
}

/// <summary>An <see cref="AsyncLocal{T}"/> caller scope for in-memory subjects and fakes.</summary>
/// <param name="fallback">The caller seen when no scope is active; a local owner by default.</param>
public sealed class AmbientCallerScope(Caller? fallback = null) : CallerScope, ICallerAmbient
{
    /// <summary>The caller used when nothing is stamped: the person <c>owner</c> (account <c>owner</c>) in brain <c>owner</c>.</summary>
    public static readonly Caller LocalOwner = new(new Actor.Person("owner", "owner"), new BrainId("owner"));

    private readonly AsyncLocal<Caller?> _current = new();

    /// <summary>The caller of the current async flow.</summary>
    public Caller Current => _current.Value ?? fallback ?? LocalOwner;

    Caller? ICallerAmbient.Current => Current;

    /// <inheritdoc />
    public override IDisposable As(Caller caller)
    {
        ArgumentNullException.ThrowIfNull(caller);
        var previous = _current.Value;
        _current.Value = caller;
        return new Restore(this, previous);
    }

    private sealed class Restore(AmbientCallerScope scope, Caller? previous) : IDisposable
    {
        public void Dispose() => scope._current.Value = previous;
    }
}

/// <summary>A caller scope that stamps through an <see cref="EdgeAuthority"/>, as a host edge does (for example <c>TestOS.Edge</c>).</summary>
/// <param name="edge">The authority that enters the caller.</param>
public sealed class EdgeCallerScope(EdgeAuthority edge) : CallerScope
{
    /// <inheritdoc />
    public override IDisposable As(Caller caller) => edge.Enter(caller);
}
