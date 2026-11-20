using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using DigitalBrain.Capabilities.Compute;
using Orleans;
using Orleans.Runtime;

namespace DigitalBrain.Runtime.Orleans.Compute;

/// <summary>Orleans RequestContext bridge for <see cref="Trace"/>.</summary>
public static class TraceRequestContext
{
    /// <summary>The Orleans RequestContext key carrying the active <see cref="Trace"/>.</summary>
    public const string RequestContextKey = "intochatos.trace";

    /// <summary>Parses a cost origin <see cref="NeuronId"/> from a brain identifier and key.</summary>
    public static NeuronId OriginOf(string brainId, string osKey) => TraceContext.OriginOf(brainId, osKey);

    [ModuleInitializer]
    [SuppressMessage("Usage", "CA2255:The 'ModuleInitializer' attribute should not be used in libraries", Justification = "Registers the Orleans RequestContext bridge for TraceContext without runtime reflection.")]
    internal static void RegisterBridge()
        => TraceContext.RegisterRequestContextAccessor(RequestContext.Get, RequestContext.Set, RequestContext.Remove);
}

/// <summary>Outgoing grain call filter that copies <see cref="TraceContext.Current"/> into Orleans <see cref="RequestContext"/>.</summary>
public sealed class TraceOutgoingFilter : IOutgoingGrainCallFilter
{
    /// <inheritdoc />
    public async Task Invoke(IOutgoingGrainCallContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (TraceContext.Current is { } trace && RequestContext.Get(TraceRequestContext.RequestContextKey) is null)
        {
            RequestContext.Set(TraceRequestContext.RequestContextKey, trace);
        }
        await context.Invoke().ConfigureAwait(false);
    }
}
