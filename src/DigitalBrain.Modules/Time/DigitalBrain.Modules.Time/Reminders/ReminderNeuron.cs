using DigitalBrain.Framework;
using DigitalBrain.Time.Reminders.Signals;
using Orleans.Runtime;
using Orleans.Timers;
namespace DigitalBrain.Time.Reminders;

[GrainType("reminder")]
internal sealed class ReminderNeuron(TimeProvider time, IReminderRegistry reminders, TimeInventoryWriter inventory) : Neuron, IReminder, IRemindable
{
    private const string Name = "tick";
    private ITimeInventory? _inventory;

    public async Task Start(TimeSpan dueTime, TimeSpan period)
    {
        _inventory = await inventory.Track(new("reminder", this.GetPrimaryKeyString(), time.GetUtcNow() + dueTime, period));
        // Orleans validates due time and the configured minimum period before writing the registration.
        await reminders.RegisterOrUpdateReminder(this.GetGrainId(), Name, dueTime, period);
    }

    public async Task Stop()
    {
        var reminder = await reminders.GetReminder(this.GetGrainId(), Name);
        if (reminder is not null) { await reminders.UnregisterReminder(this.GetGrainId(), reminder); }
        await inventory.Remove(_inventory, "reminder", this.GetPrimaryKeyString());
    }

    public async Task ReceiveReminder(string reminderName, TickStatus status)
    {
        if (reminderName == Name)
        {
            await PublishAsync(new ReminderTick(this.GetPrimaryKeyString(), time.GetUtcNow()));
            return;
        }

        if (reminderName != "job") { return; }

        // Retire registrations left by the removed automation job runner.
        var obsolete = await reminders.GetReminder(this.GetGrainId(), reminderName);
        if (obsolete is not null) { await reminders.UnregisterReminder(this.GetGrainId(), obsolete); }
    }
}
