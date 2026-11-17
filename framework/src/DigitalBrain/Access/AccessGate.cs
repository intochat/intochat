using DigitalBrain.Kernel;
using DigitalBrain.Invocation;

namespace DigitalBrain.Access;

/// <summary>
/// Synchronous admission gate for neuron operations. Evaluated after binding and before neuron code executes.
/// </summary>
internal sealed class AccessGate(ICallerAmbient callers)
{
    /// <summary>
    /// Evaluates access permissions for the target neuron and bound call.
    /// </summary>
    /// <returns>The caller under which the operation runs.</returns>
    /// <exception cref="UnauthorizedAccessException">Access is denied or the call carries no caller.</exception>
    public Caller Admit(NeuronId target, CallDescriptor call)
    {
        ArgumentNullException.ThrowIfNull(call);
        if (callers.Current is not { } caller)
        {
            throw new UnauthorizedAccessException($"{call.Contract.Name}.{call.Method.Name} on {target}: the call carries no caller.");
        }

        var decision = AccessPolicy.Decide(caller, target, call.Access);
        if (decision.Allowed) { return caller; }

        throw new UnauthorizedAccessException($"{decision.Explanation} ({call.Contract.Name}.{call.Method.Name} on {target.Type})");
    }
}
