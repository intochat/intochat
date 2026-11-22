namespace DigitalBrain.Access;

/// <summary>Describes a bound neuron operation being evaluated by an <see cref="IAccessAdmissionStage"/>.</summary>
/// <param name="Contract">The contract interface type being invoked.</param>
/// <param name="Method">The method name on the contract.</param>
/// <param name="Access">The declared operation access level.</param>
/// <param name="ReadOnly">Whether the operation is marked <see cref="DigitalBrain.Contracts.ReadOnlyCallAttribute"/>.</param>
public readonly record struct AccessCall(Type Contract, string Method, OperationAccess Access, bool ReadOnly);

/// <summary>
/// An optional admission stage invoked by <see cref="AccessGate"/> after the cross-brain wall,
/// <see cref="AccessPolicy"/>, binding checks, and <see cref="DigitalBrain.Components.OfferedAttribute"/> offer checks.
/// Implementations read the current caller from <see cref="ICallerAmbient"/>.
/// Runs identically on both in-memory (<c>TestOS</c>) and clustered (Orleans silo) runtimes.
/// </summary>
public interface IAccessAdmissionStage
{
    /// <summary>
    /// Evaluates additional admission rules for the current caller invoking <paramref name="call"/> on
    /// <paramref name="target"/>. Throw <see cref="UnauthorizedAccessException"/> to deny the call.
    /// </summary>
    ValueTask AdmitAsync(NeuronId target, AccessCall call, CancellationToken cancellationToken = default);
}
