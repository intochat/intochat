namespace DigitalBrain;

// Who may call a contract method; the kernel checks it once per call against the stamped caller.
// Unmarked is the owner rule: the caller's brain is the brain the neuron's key starts with.
// On an interface it is the contract's default (and the policy of its inherited Watch/Unwatch);
// on a method it wins over the interface.
[AttributeUsage(AttributeTargets.Interface | AttributeTargets.Method, Inherited = false)]
public abstract class AccessAttribute : Attribute;

// Any authenticated caller; no brain scope, no grants.
public sealed class PublicAttribute : AccessAttribute;

// One neuron serves every brain: each caller acts as its own brain.
public sealed class SharedAttribute : AccessAttribute;

// The key is an account id: only that account's callers, acting as their selected brain.
public sealed class PerAccountAttribute : AccessAttribute;

// Reachable only from inside the cluster (module code), never from an edge caller.
public sealed class InternalAttribute : AccessAttribute;
