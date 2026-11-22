using DigitalBrain.Components;
using DigitalBrain.Contracts;

namespace DigitalBrain.Units;

/// <summary>How an install ended.</summary>
public enum InstallStatus
{
    /// <summary>Approved and recorded for the first time.</summary>
    Installed,

    /// <summary>The recorded needs are the same but the slot bindings changed; the new bindings were saved without asking.</summary>
    Updated,

    /// <summary>The recorded needs are the same; nothing was asked.</summary>
    Unchanged,

    /// <summary>The person did not approve; nothing was recorded.</summary>
    Denied,

    /// <summary>The request cannot be installed (unknown alias, unbound slot).</summary>
    Refused,

    /// <summary>The person was asked; the install completes when the plan is resolved (<see cref="InstallOutcome.Reason"/> is the plan id).</summary>
    Pending,
}

/// <summary>A request to install a synapse app.</summary>
/// <param name="Alias">The state alias.</param>
/// <param name="Bindings">Slot name to connection key text.</param>
public sealed record InstallRequest(string Alias, IReadOnlyDictionary<string, string> Bindings);

/// <summary>The result of <see cref="IInstalls.Install"/>.</summary>
/// <param name="Status">How it ended.</param>
/// <param name="Sentence">The needs sentence; empty when the alias is unknown.</param>
/// <param name="Reason">
/// Why it was refused, the plan id when <see cref="InstallStatus.Pending"/>, or <c>hooks failed: ...</c> when an unchanged or updated install's
/// host hooks failed; null otherwise.
/// </param>
public sealed record InstallOutcome(InstallStatus Status, string Sentence, string? Reason);

/// <summary>An installed synapse app or bound component, as the person approved it.</summary>
/// <param name="Alias">The state alias or component id.</param>
/// <param name="NeedsHash">The <see cref="SynapseNeeds.Hash"/> that was approved.</param>
/// <param name="Bindings">Slot name to connection key text (legacy synapse bindings).</param>
/// <param name="Schedules">The schedule texts it runs on.</param>
/// <param name="ConsentedAt">When the person approved.</param>
/// <param name="Typed">Typed slot bindings, or null for records written before typed bindings existed.</param>
public sealed record InstallRecord(
    string Alias,
    string NeedsHash,
    IReadOnlyDictionary<string, string> Bindings,
    IReadOnlyList<string> Schedules,
    DateTimeOffset ConsentedAt,
    Binding[]? Typed = null)
{
    /// <summary>
    /// All active bindings for this record: explicit <see cref="Typed"/> entries plus any legacy <see cref="Bindings"/>
    /// entries not already present in <see cref="Typed"/>, read as <see cref="BindingTarget.Connection"/>.
    /// </summary>
    public IReadOnlyList<Binding> EffectiveBindings()
    {
        var result = new List<Binding>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        if (Typed is { Length: > 0 })
        {
            foreach (var binding in Typed)
            {
                if (seen.Add(binding.Slot)) { result.Add(binding); }
            }
        }
        if (Bindings is { Count: > 0 })
        {
            foreach (var (slot, connectionKey) in Bindings)
            {
                if (seen.Add(slot)) { result.Add(new Binding(slot, new BindingTarget.Connection(connectionKey))); }
            }
        }
        return result;
    }
}

/// <summary>An install waiting for the person's answer.</summary>
/// <param name="PlanId">The confirmation plan id.</param>
/// <param name="Alias">The state alias.</param>
/// <param name="Bindings">Slot name to connection key text.</param>
/// <param name="NeedsHash">The <see cref="SynapseNeeds.Hash"/> put to the person.</param>
/// <param name="Sentence">The needs sentence put to the person.</param>
public sealed record PendingInstall(string PlanId, string Alias, IReadOnlyDictionary<string, string> Bindings, string NeedsHash, string Sentence);

/// <summary>The installs of one brain, at <c>{brain}/synapse.installs/default</c>.</summary>
[NeuronAlias("synapse.installs")]
public interface IInstalls
{
    /// <summary>
    /// Installs a synapse app. When its needs are new or changed the person is asked and the call returns
    /// <see cref="InstallStatus.Pending"/> at once; the install is recorded when the plan's <c>ConfirmationResolved</c> approves it.
    /// </summary>
    /// <param name="request">The alias and its slot bindings.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>How it ended, or that it waits for the person.</returns>
    Task<InstallOutcome> Install(InstallRequest request, CancellationToken ct);

    /// <summary>Whether <see cref="Install"/> would accept <paramref name="request"/>: the reason it would refuse it (unknown alias, unbound slot, a binding the host rejects), or null. Changes nothing.</summary>
    /// <param name="request">The alias and its slot bindings.</param>
    /// <param name="ct">Cancellation.</param>
    [ReadOnlyCall]
    Task<string?> Validate(InstallRequest request, CancellationToken ct);

    /// <summary>Retires an installed app and cancels its pending asks; an alias that is neither is ignored.</summary>
    /// <param name="alias">The state alias.</param>
    /// <param name="ct">Cancellation.</param>
    Task Retire(string alias, CancellationToken ct);

    /// <summary>Uninstalls an installed app and cancels its pending asks; an alias that is neither is ignored.</summary>
    /// <param name="alias">The state alias.</param>
    /// <param name="ct">Cancellation.</param>
    Task Uninstall(string alias, CancellationToken ct = default);

    /// <summary>
    /// Cancels every pending ask of <paramref name="alias"/>, journaling <see cref="InstallDenied"/> with reason <c>superseded</c> for each;
    /// the record, if any, stays. A later answer to a cancelled ask is ignored.
    /// </summary>
    /// <param name="alias">The state alias.</param>
    /// <param name="ct">Cancellation.</param>
    Task CancelPending(string alias, CancellationToken ct);

    /// <summary>The record of <paramref name="alias"/>, or null when it is not installed.</summary>
    /// <param name="alias">The state alias.</param>
    /// <param name="ct">Cancellation.</param>
    [ReadOnlyCall]
    Task<InstallRecord?> Describe(string alias, CancellationToken ct);

    /// <summary>Every installed app.</summary>
    /// <param name="ct">Cancellation.</param>
    [ReadOnlyCall]
    Task<IReadOnlyList<InstallRecord>> List(CancellationToken ct);

    /// <summary>The installs waiting for the person.</summary>
    /// <param name="ct">Cancellation.</param>
    [ReadOnlyCall]
    Task<IReadOnlyList<PendingInstall>> Pending(CancellationToken ct);

    /// <summary>Re-runs host wiring hooks for an installed alias against its current needs.</summary>
    /// <param name="alias">The state alias.</param>
    /// <param name="ct">Cancellation.</param>
    Task Rewire(string alias, CancellationToken ct = default);

    /// <summary>Records typed slot bindings for a component, creating or updating its install record.</summary>
    /// <param name="component">The component id.</param>
    /// <param name="bindings">The typed slot bindings.</param>
    /// <param name="ct">Cancellation.</param>
    Task BindComponent(ComponentId component, Binding[] bindings, CancellationToken ct = default);
}
