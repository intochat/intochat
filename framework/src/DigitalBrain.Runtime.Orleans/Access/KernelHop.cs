using DigitalBrain.Access;
using DigitalBrain.Capabilities.Compute;

namespace DigitalBrain.Runtime.Orleans.Access;

/// <summary>
/// The kernel's own hop into a brain (resolving a unit or a slot for it): enters the kernel caller of that brain and, when no
/// trace is running, starts one paid by the brain, as the edge does for a request.
/// </summary>
internal static class KernelHop
{
    public static IDisposable Enter(BrainId brain)
    {
        var caller = OrleansCallerAmbient.Instance.Enter(Caller.OfKernel(brain));
        if (TraceContext.Current is not null) { return caller; }
        var trace = TraceContext.Enter(TraceContext.Begin(brain.Value, brain.Value, new NeuronId(brain, "edge", "request")));
        return new Both(trace, caller);
    }

    private sealed class Both(IDisposable inner, IDisposable outer) : IDisposable
    {
        public void Dispose()
        {
            inner.Dispose();
            outer.Dispose();
        }
    }
}
