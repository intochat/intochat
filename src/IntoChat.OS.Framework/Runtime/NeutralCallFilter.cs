// os-only: maps the neutral scheduling attributes onto Orleans in the os silo host.
using DigitalBrain.Runtime.Orleans.Hosting;
using IntoChat.OS.Framework.Modules;
using Microsoft.Extensions.Logging;
using Orleans.Serialization.Invocation;

namespace IntoChat.OS.Framework;

// Caller-side half of the mapping. A [OneWayCall] is sent and not awaited: the caller resumes at once and a fault
// is logged, never thrown. The message keeps its invokable id and still carries a response, which nothing awaits.
// A [CallTimeout] bounds the caller's wait; Orleans' own response timeout still applies, so a longer timeout
// needs the messaging timeout raised to the largest declared one (the provider does this in PR 5).
public sealed class NeutralCallFilter(ILogger<NeutralCallFilter> logger) : IOutgoingGrainCallFilter
{
    public Task Invoke(IOutgoingGrainCallContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var method = context.InterfaceMethod;
        if (method is null) { return context.Invoke(); }
        if (NeutralCallMapping.IsOneWay(method))
        {
            _ = context.Invoke().ContinueWith(
                call => logger.LogWarning(call.Exception, "One-way call {Interface}.{Method} failed.", context.InterfaceName, context.MethodName),
                CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            // The caller's chain completes now with an empty response; the request is already on its way.
            context.Response = Response.Completed;
            return Task.CompletedTask;
        }
        return NeutralCallMapping.TimeoutOf(method) is { } timeout ? context.Invoke().WaitAsync(timeout) : context.Invoke();
    }
}
