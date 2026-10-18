using IntoChat.OS.Framework;

namespace IntoChat.OS.Authority.Enforcement;

internal sealed class CallerContextScope : IDisposable
{
    private readonly object? _previous;
    private bool _disposed;

    private CallerContextScope(CallerContext caller)
    {
        _previous = RequestContext.Get(CallerContextStamper.RequestContextKey);
        CallerContextStamper.Stamp(caller);
    }

    internal static IDisposable Scoped(CallerContext caller) => new CallerContextScope(caller);

    public void Dispose()
    {
        if (_disposed) { return; }
        _disposed = true;
        if (_previous is null) { RequestContext.Remove(CallerContextStamper.RequestContextKey); }
        else { RequestContext.Set(CallerContextStamper.RequestContextKey, _previous); }
    }
}
