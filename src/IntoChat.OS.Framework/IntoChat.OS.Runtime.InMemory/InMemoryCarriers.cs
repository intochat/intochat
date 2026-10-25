using IntoChat.OS.Framework;
using IntoChat.OS.Contracts.Compute;
using IntoChat.OS.Framework.Compute;
using IntoChat.OS.Runtime;
using ICallerContextCarrier = DigitalBrain.Spi.ICallerContextCarrier;

namespace IntoChat.OS.Runtime.InMemory;

// The in-memory runtime's own caller and trace carriers, built on the Framework's public request-context keys.
internal sealed class InMemoryCallerCarrier : ICallerContextCarrier
{
    public IDisposable Suppress()
    {
        var stamped = RequestContext.Get(CallerContextStamper.RequestContextKey);
        RequestContext.Remove(CallerContextStamper.RequestContextKey);
        return new Restore(stamped);
    }

    private sealed class Restore(object? stamped) : IDisposable
    {
        public void Dispose()
        {
            if (stamped is null) { RequestContext.Remove(CallerContextStamper.RequestContextKey); }
            else { RequestContext.Set(CallerContextStamper.RequestContextKey, stamped); }
        }
    }
}

internal sealed class InMemoryTraceCarrier : ITraceCarrier
{
    public Trace? Current => TraceContext.Current;

    public IDisposable Enter(Trace trace) => TraceContext.Enter(trace);
}
