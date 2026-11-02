// os-only: flows the os Trace across grain calls.
using IntoChat.OS.Contracts.Compute;
using IntoChat.OS.Contracts.Configuration;
using IntoChat.OS.Framework.Modules;
using Orleans;

namespace IntoChat.OS.Framework.Compute;

public sealed class TraceOutgoingFilter : IOutgoingGrainCallFilter
{
    public async Task Invoke(IOutgoingGrainCallContext context)
    {
        if (TraceContext.Current is { } trace && Orleans.Runtime.RequestContext.Get(TraceContext.RequestContextKey) is null)
        {
            Orleans.Runtime.RequestContext.Set(TraceContext.RequestContextKey, trace);
        }
        await context.Invoke();
    }
}
