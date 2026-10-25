using IntoChat.OS.Runtime;
using Orleans.Timers;

namespace IntoChat.OS.Kernel.Composition;

// Transitional (06-03, deleted in 06-08): the Framework ReminderNeuron registered its reminders under its own grain id
// reminder/{brain}/{key}. The plain ReminderNeuron runs on the runtime's host grain neuron.host/{brain}/reminder/{key}, so a
// row registered before the migration would target a grain type nothing serves any more. This grain takes that type: when a
// legacy row fires it registers the same reminder name and period on the plain neuron's host grain, delivers this tick there
// (so the reminder still fires once now), and removes the legacy row. Grain state is not carried over (SAFE-01 clean break).
[GrainType(LegacyType)]
internal sealed class RetiredReminderGrain(IReminderRegistry reminders) : Grain, IRemindable
{
    internal const string LegacyType = "reminder";
    internal const string HostType = "neuron.host";
    internal const string Tick = "tick";

    public async Task ReceiveReminder(string reminderName, TickStatus status)
    {
        var legacy = this.GetGrainId();
        // Only the periodic "tick" moves; any other legacy row (the retired automation "job") is just removed.
        if (reminderName == Tick && Target(this.GetPrimaryKeyString()) is { } target)
        {
            var period = status.Period > TimeSpan.Zero ? status.Period : TimeSpan.FromMinutes(1);
            await reminders.RegisterOrUpdateReminder(target, reminderName, period, period);
            await GrainFactory.GetGrain<IRemindable>(target).ReceiveReminder(reminderName, status);
        }
        if (await reminders.GetReminder(legacy, reminderName) is { } row) { await reminders.UnregisterReminder(legacy, row); }
        DeactivateOnIdle();
    }

    // The plain reminder's host grain for the legacy key "{brain}/{key}"; null for a key outside every brain.
    internal static GrainId? Target(string legacyKey)
    {
        if (NeuronAddress.ScopeOf(legacyKey) is not { } brain) { return null; }
        var id = new global::DigitalBrain.NeuronId(new global::DigitalBrain.BrainId(brain), LegacyType, legacyKey[(brain.Length + 1)..]);
        return GrainId.Create(HostType, id.ToString());
    }
}
