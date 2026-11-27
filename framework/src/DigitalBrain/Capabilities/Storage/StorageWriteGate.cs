using DigitalBrain.Access;
using DigitalBrain.Confirmation;

namespace DigitalBrain.Capabilities.Storage.Gating;

/// <summary>
/// Policy controlling whether same-brain privileged callers bypass confirmation on a storage neuron.
/// </summary>
public enum StorageWritePolicy
{
    /// <summary>Every non-dry-run write without an approved runner permit requires confirmation (fail-closed).</summary>
    AlwaysConfirm = 0,

    /// <summary>
    /// Workspace policy: direct <see cref="Actor.Person"/>, <see cref="Actor.Module"/>, or <see cref="Actor.Kernel"/>
    /// callers of the same brain bypass confirmation; <see cref="Actor.App"/>, <see cref="Actor.Agent"/>, relayed persons,
    /// cross-brain callers, and unstamped callers never bypass.
    /// </summary>
    WorkspaceDirect = 1,
}

/// <summary>
/// Unified fail-closed write authorization gate for storage neurons.
/// </summary>
public static class StorageWriteGate
{
    /// <summary>
    /// Authorizes a storage write <paramref name="plan"/> before the storage neuron executes it.
    /// Evaluates optional same-brain workspace bypass policy, single-use approved runner permits,
    /// dry-run plans, and fail-closed confirmation.
    /// </summary>
    /// <exception cref="Denied">Thrown when confirmation is required and either <paramref name="confirmations"/> is missing or the outcome is not <see cref="ConfirmationOutcome.Approved"/>.</exception>
    public static async Task AuthorizeAsync(
        NeuronId storageNeuron,
        IStorage storage,
        WritePlan plan,
        Caller? caller,
        IStorageWriteAuthorizer? authorizer,
        IConfirmations? confirmations,
        StorageWritePolicy policy = StorageWritePolicy.AlwaysConfirm,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(storage);
        ArgumentNullException.ThrowIfNull(plan);

        if (policy == StorageWritePolicy.WorkspaceDirect && CanBypassWorkspaceConfirmation(storageNeuron.Brain, caller))
        {
            return;
        }

        if (authorizer is not null && await authorizer.Consume(storageNeuron, plan, ct).ConfigureAwait(true))
        {
            return;
        }

        if (plan.DryRun)
        {
            return;
        }

        if (confirmations is null)
        {
            throw new Denied(plan.Source ?? plan.TargetFolder);
        }

        var descriptor = await storage.Describe(ct).ConfigureAwait(true);
        var connection = new CapabilityConnection<IStorage>("storage", descriptor.Label, storage);
        var runner = new StorageWriteRunner(
            (_, _) => Task.FromResult(connection),
            (_, _, _) => throw new InvalidOperationException("A confirmation does not execute writes."));
        var question = await runner.Confirmation(plan, ct).ConfigureAwait(true);
        var answer = await confirmations.Confirm(question, ct).ConfigureAwait(true);
        if (answer.Outcome != ConfirmationOutcome.Approved)
        {
            throw new Denied(plan.Source ?? plan.TargetFolder);
        }
    }

    internal static bool CanBypassWorkspaceConfirmation(BrainId neuronBrain, Caller? caller)
    {
        if (caller is null || caller.Brain != neuronBrain)
        {
            return false;
        }

        return caller.Actor switch
        {
            Actor.Person => caller.Direct,
            Actor.Module or Actor.Kernel => true,
            _ => false,
        };
    }
}
