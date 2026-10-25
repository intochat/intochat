using DigitalBrain.Capabilities.Storage;
using DigitalBrain.Confirmation;
using Microsoft.Extensions.DependencyInjection;

namespace DigitalBrain.Runtime.Orleans.Operations;

/// <summary>
/// The default <see cref="IOperationGate"/> (07 D-03): a storage <see cref="WritePlan"/> is put to the host's
/// <see cref="IConfirmations"/> under the brain policy (by default delete, move between connections and overwrite ask), and runs
/// only when approved. Without a registered <see cref="IConfirmations"/>, and for any other plan, every operation runs.
/// </summary>
/// <param name="services">The silo services.</param>
public sealed class ConfirmingOperationGate(IServiceProvider services) : IOperationGate
{
    /// <inheritdoc />
    public async Task<OperationGateDecision> Check(OperationRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Plan is not WritePlan write || services.GetService<IConfirmations>() is not { } confirmations) { return OperationGateDecision.Allow; }
        var runner = new RuntimeOperations(services, request.Brain, request.Origin).Runner();
        var refused = await StorageConfirmationGate.Check(runner, confirmations, write, ct).ConfigureAwait(false);
        return refused is null ? OperationGateDecision.Allow : OperationGateDecision.Deny(refused);
    }
}
