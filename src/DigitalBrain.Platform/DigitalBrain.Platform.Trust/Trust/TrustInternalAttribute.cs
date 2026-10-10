namespace DigitalBrain.Platform.Trust;

// An unsigned trust write (R63): only the trust cluster's own grains call it. A machine reaches the same write
// through a signed call, which the trust side checks against the machine's binding to the account.
[AttributeUsage(AttributeTargets.Method)]
internal sealed class TrustInternalAttribute : Attribute;

// On a separate trust host, a call from outside the cluster (a machine's gateway client) to a trust-internal
// write is refused; only grains of the trust cluster may make it.
internal sealed class TrustBoundaryFilter : IIncomingGrainCallFilter
{
    public Task Invoke(IIncomingGrainCallContext context)
    {
        if (context.InterfaceMethod.GetCustomAttributes(typeof(TrustInternalAttribute), inherit: true).Length > 0
            && (context.SourceId is not { } source || source.IsClient()))
        { throw new UnauthorizedAccessException($"{context.InterfaceMethod.DeclaringType?.Name}.{context.InterfaceMethod.Name} is written only through a signed call."); }
        return context.Invoke();
    }
}
