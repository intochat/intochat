namespace DigitalBrain.Framework;

// R146: whether an app of one brain may use a connection of another, through a binding recorded when the app was
// shared (R134). The default binds nothing; the Platform's answers from the owner's connection, and only while the
// caller and the connection's owner are both members of the caller's brain (R155).
public interface IConnectionBindings
{
    ValueTask<bool> IsBound(CallerContext caller, string connectionKey, CancellationToken cancellationToken = default);
}

internal sealed class NoConnectionBindings : IConnectionBindings
{
    public ValueTask<bool> IsBound(CallerContext caller, string connectionKey, CancellationToken cancellationToken = default)
        => ValueTask.FromResult(false);
}
