namespace DigitalBrain.Testing.Conformance;

/// <summary>A caller the secrets@1 and identity@1 kits present to the subject, as the runtime would stamp it (D-15).</summary>
/// <param name="PrincipalId">The calling principal.</param>
/// <param name="AccountId">The account paying for the call.</param>
/// <param name="BrainId">The brain (workspace) the call runs in.</param>
/// <param name="AppId">The app making the call, or null for a user.</param>
/// <param name="ConversationId">The conversation an app call runs for, if any.</param>
public sealed record KitCaller(string PrincipalId, string AccountId, string BrainId, string? AppId = null, string? ConversationId = null);

/// <summary>
/// Switches the caller the subject sees. secrets@1 and identity@1 take no caller parameter: the implementation reads the
/// caller the runtime stamped on the call. A kit subclass implements <see cref="As"/> by stamping <paramref name="caller"/>
/// the way its runtime does, until the returned scope is disposed.
/// </summary>
public abstract class CallerScope
{
    /// <summary>Makes subsequent calls on this async flow run as <paramref name="caller"/> until the result is disposed.</summary>
    public abstract IDisposable As(KitCaller caller);
}

/// <summary>An <see cref="AsyncLocal{T}"/> caller scope for in-memory subjects and fakes.</summary>
/// <param name="fallback">The caller seen when no scope is active; a local owner by default.</param>
public sealed class AmbientCallerScope(KitCaller? fallback = null) : CallerScope
{
    /// <summary>The caller used when nothing is stamped: principal, account and brain <c>owner</c>.</summary>
    public static readonly KitCaller LocalOwner = new("owner", "owner", "owner");

    private readonly AsyncLocal<KitCaller?> _current = new();

    /// <summary>The caller of the current async flow.</summary>
    public KitCaller Current => _current.Value ?? fallback ?? LocalOwner;

    /// <inheritdoc />
    public override IDisposable As(KitCaller caller)
    {
        ArgumentNullException.ThrowIfNull(caller);
        var previous = _current.Value;
        _current.Value = caller;
        return new Restore(this, previous);
    }

    private sealed class Restore(AmbientCallerScope scope, KitCaller? previous) : IDisposable
    {
        public void Dispose() => scope._current.Value = previous;
    }
}
