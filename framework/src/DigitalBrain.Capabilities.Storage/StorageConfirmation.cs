using DigitalBrain.Confirmation;

namespace DigitalBrain.Capabilities.Storage;

/// <summary>
/// The confirmation question for a storage <see cref="WritePlan"/> (07 D-03, D-22, UI-SPEC "Confirmation"). Destructive: delete,
/// move between two connections, and any plan with <see cref="ConflictPolicy.Overwrite"/>. Not destructive: create folder, write
/// without overwrite, rename, copy, move within one connection; those are only asked under <see cref="ConfirmationPolicy.Always"/>.
/// Texts name items and storages by display name only, never by path or id (T-07-08).
/// </summary>
public static class StorageConfirmation
{
    /// <summary>True when <paramref name="plan"/> is destructive; <paramref name="crossConnection"/> says whether source and target folder live on different connections.</summary>
    public static bool IsDestructive(WritePlan plan, bool crossConnection)
    {
        ArgumentNullException.ThrowIfNull(plan);
        return plan.Kind == WriteKind.Delete
            || plan.Conflict == ConflictPolicy.Overwrite
            || (plan.Kind == WriteKind.Move && crossConnection);
    }

    /// <summary>The question for <paramref name="plan"/>.</summary>
    /// <param name="planId">The confirmation plan id.</param>
    /// <param name="plan">The storage plan.</param>
    /// <param name="crossConnection">True when source and target folder live on different connections.</param>
    /// <param name="name">The display name of the item acted on (or created).</param>
    /// <param name="storage">The display name of the target storage (the source storage for delete).</param>
    /// <param name="sourceStorage">The display name of the source storage.</param>
    /// <param name="folder">The display name of the target folder.</param>
    public static ConfirmationPlan For(string planId, WritePlan plan, bool crossConnection, string name, string storage, string sourceStorage, string folder)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(planId);
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(storage);
        ArgumentNullException.ThrowIfNull(sourceStorage);
        ArgumentNullException.ThrowIfNull(folder);
        if (plan.Kind == WriteKind.Delete)
        {
            return new(planId, ConfirmationAction.Delete, true, $"Delete {name}?", $"This removes {name} from {storage}. You cannot undo this.", "Delete", "Keep", name);
        }
        if (plan.Conflict == ConflictPolicy.Overwrite)
        {
            return new(planId, ConfirmationAction.Overwrite, true, $"Replace {name}?",
                $"A file with this name already exists in {folder}. Replacing it cannot be undone.", "Replace", "Keep existing", name);
        }
        if (plan.Kind == WriteKind.Move && crossConnection)
        {
            return new(planId, ConfirmationAction.Move, true, $"Move {name} to {storage}?",
                $"{name} is copied to {storage}, checked, then removed from {sourceStorage}.", "Move", "Cancel", name);
        }
        var (action, verb) = plan.Kind switch
        {
            WriteKind.Move => (ConfirmationAction.Move, "Move"),
            WriteKind.Copy => (ConfirmationAction.Copy, "Copy"),
            WriteKind.Rename => (ConfirmationAction.Rename, "Rename"),
            WriteKind.CreateFolder => (ConfirmationAction.Other, "Create"),
            _ => (ConfirmationAction.Other, "Save"),
        };
        return new(planId, action, false, $"{verb} {name}?", string.Empty, verb, "Cancel", name);
    }
}

/// <summary>
/// The storage operation gate (07 D-03): before a <see cref="WritePlan"/> runs, asks <see cref="IConfirmations"/> under the brain
/// policy. The runtime operation entity and TestOS both use it. Dry runs are never asked (they change nothing).
/// </summary>
public static class StorageConfirmationGate
{
    /// <summary>Null when <paramref name="plan"/> may run, else why it was refused.</summary>
    public static async Task<string?> Check(StorageWriteRunner runner, IConfirmations confirmations, WritePlan plan, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(runner);
        ArgumentNullException.ThrowIfNull(confirmations);
        ArgumentNullException.ThrowIfNull(plan);
        if (plan.DryRun) { return null; }
        var question = await runner.Confirmation(plan, ct).ConfigureAwait(false);
        var answer = await confirmations.Confirm(question, ct).ConfigureAwait(false);
        return answer.Outcome switch
        {
            ConfirmationOutcome.Approved => null,
            ConfirmationOutcome.Expired => "The confirmation expired.",
            _ when answer.Reason == ConfirmationDenyReason.SelfApproval => "Only a person can approve this.",
            _ => "The confirmation was denied.",
        };
    }
}
