using DigitalBrain.Components;
using DigitalBrain.Kernel;

namespace DigitalBrain.Access;

/// <summary>
/// Admission gate for neuron operations. Evaluated after binding and before neuron code executes.
/// Enforces the cross-brain wall, <see cref="AccessPolicy"/>, and <see cref="OfferedAttribute"/> per-resource offers.
/// </summary>
internal sealed class AccessGate(
    ICallerAmbient callers,
    Func<NeuronId, string, CancellationToken, ValueTask<Role?>>? roleLookup = null,
    IBindingIndex? bindingIndex = null,
    BindingEnforcementMode bindingMode = BindingEnforcementMode.Off,
    Action<MissingBindingEvent>? onMissingBinding = null,
    IReadOnlySet<string>? privilegedComponents = null)
{
    internal const string OffersType = "component.offers";

    /// <summary>
    /// Evaluates access permissions for the target neuron and bound call.
    /// Same-brain and kernel calls complete synchronously without allocating a state machine.
    /// </summary>
    /// <returns>The caller under which the operation runs.</returns>
    /// <exception cref="UnauthorizedAccessException">Access is denied or the call carries no caller.</exception>
    public ValueTask<Caller> Admit(NeuronId target, CallDescriptor call, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(call);
        if (callers.Current is not { } caller)
        {
            throw new UnauthorizedAccessException($"{call.Contract.Name}.{call.Method.Name} on {target}: the call carries no caller.");
        }

        // Fast path: same-brain or kernel calls skip wall and offer checks synchronously.
        if (caller.Actor is Actor.Kernel || caller.Brain == target.Brain)
        {
            var sameBrain = AccessPolicy.Decide(caller, target, call.Access);
            if (!sameBrain.Allowed)
            {
                throw new UnauthorizedAccessException($"{sameBrain.Explanation} ({call.Contract.Name}.{call.Method.Name} on {target.Type})");
            }

            CheckBinding(caller, target, call);
            return ValueTask.FromResult(caller);
        }

        // Cross-brain wall for non-system targets.
        if (!target.Brain.IsSystem)
        {
            EnsureCrossBrainWall(target, call);
        }

        var decision = AccessPolicy.Decide(caller, target, call.Access);
        if (!decision.Allowed)
        {
            throw new UnauthorizedAccessException($"{decision.Explanation} ({call.Contract.Name}.{call.Method.Name} on {target.Type})");
        }

        // [Offered] check for foreign callers on non-system targets (skip self-lookup on component.offers).
        if (!target.Brain.IsSystem && IsOffered(call) && !string.Equals(target.Type, OffersType, StringComparison.Ordinal))
        {
            return AdmitOfferedAsync(caller, target, call.ReadOnly, call.Contract.Name, call.Method.Name, cancellationToken);
        }

        return ValueTask.FromResult(caller);
    }

    /// <summary>
    /// Enforces the cross-brain wall: a caller of another brain reaches only a <see cref="OperationAccess.Shared"/> or
    /// <see cref="OperationAccess.Public"/> operation on a contract marked <see cref="ShareableAttribute"/> (and whose
    /// declaring interface opted in as well).
    /// </summary>
    public void EnsureCrossBrainWall(NeuronId target, CallDescriptor call)
    {
        ArgumentNullException.ThrowIfNull(call);
        if (callers.Current is not { } caller || !IsForeign(caller, target)) { return; }

        var shareable = typeof(ShareableAttribute);
        var crosses = call.Access is OperationAccess.Public or OperationAccess.Shared
            && call.Contract.IsDefined(shareable, inherit: false)
            // A method the contract inherits crosses only when the interface that declares it opted in as well: Shared on
            // a derived contract must not open the methods of a base contract written for its own brain.
            && (call.Method.DeclaringType == call.Contract || call.Method.DeclaringType?.IsDefined(shareable, inherit: false) == true);
        if (!crosses)
        {
            throw CrossBrainViolation(caller.Brain, target.Brain);
        }
    }

    internal static UnauthorizedAccessException CrossBrainViolation(BrainId callerBrain, BrainId targetBrain)
        => new($"Cross-brain security violation: Caller in brain '{callerBrain.Value}' cannot invoke grain in brain '{targetBrain.Value}'.");

    private static bool IsForeign(Caller caller, NeuronId target)
        => caller.Brain != target.Brain && !target.Brain.IsSystem && caller.Actor is not Actor.Kernel;

    private static bool IsOffered(CallDescriptor call)
        => call.Contract.IsDefined(typeof(OfferedAttribute), inherit: false)
            || (call.Method.DeclaringType is { } declaring && declaring.IsDefined(typeof(OfferedAttribute), inherit: false));

    private async ValueTask<Caller> AdmitOfferedAsync(
        Caller caller,
        NeuronId target,
        bool readOnly,
        string contractName,
        string methodName,
        CancellationToken cancellationToken)
    {
        var account = caller.For?.Account;
        if (string.IsNullOrWhiteSpace(account) || roleLookup is null)
        {
            throw new UnauthorizedAccessException($"offer.none: {contractName}.{methodName} on {target} has no offer for this caller.");
        }

        var role = await roleLookup(target, account, cancellationToken).ConfigureAwait(true);
        if (role is null)
        {
            throw new UnauthorizedAccessException($"offer.none: {contractName}.{methodName} on {target} has no offer for '{account}'.");
        }

        if (role == Role.Viewer && !readOnly)
        {
            throw new UnauthorizedAccessException($"offer.read-only: {contractName}.{methodName} on {target} requires Editor role (caller '{account}' has Viewer).");
        }

        return caller;
    }

    private void CheckBinding(Caller caller, NeuronId target, CallDescriptor call)
    {
        if (bindingMode == BindingEnforcementMode.Off
            || bindingIndex is null
            || caller.Actor is Actor.Kernel
            || caller.Brain != target.Brain
            || target.Brain.IsSystem
            || caller.Component is not { } component)
        {
            return;
        }

        if (string.Equals(target.Type, component.Value, StringComparison.Ordinal)
            || target.Type is "synapse.installs" or OffersType
            || privilegedComponents?.Contains(component.Value) == true)
        {
            return;
        }

        if (bindingIndex.IsBound(caller.Brain, component, target))
        {
            return;
        }

        onMissingBinding?.Invoke(new MissingBindingEvent(caller.Brain, component, target, $"{call.Contract.Name}.{call.Method.Name}"));

        if (bindingMode == BindingEnforcementMode.Enforce)
        {
            throw new UnauthorizedAccessException($"binding.missing: component '{component.Value}' has no binding to '{target}'.");
        }
    }
}
