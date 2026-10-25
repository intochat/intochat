using DigitalBrain.Spi;

namespace DigitalBrain.Runtime.Orleans.Hosting;

/// <summary>What the host grain gives its context for ticks: activation timers and durable reminders. Every tick is a turn on the grain.</summary>
internal interface IHostTicks
{
    void StartTimer(string name, TimeSpan due, TimeSpan? period);

    void StopTimer(string name);

    Task StartReminder(string name, TimeSpan due, TimeSpan period);

    Task StopReminder(string name);

    Task<IReadOnlyList<string>> ReminderNames();
}

/// <summary>
/// <see cref="ITimers"/> and <see cref="IReminders"/> of a hosted neuron. Timer and reminder ticks both reach
/// <see cref="IRemindedNeuron.OnReminder"/> as host grain turns, as on TestOS (04-08).
/// </summary>
internal sealed class HostTimers(IHostTicks ticks, NeuronRegistration registration) : ITimers, IReminders
{
    public void Start(string name, TimeSpan due, TimeSpan? period)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ticks.StartTimer(name, due, period);
    }

    public void Stop(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ticks.StopTimer(name);
    }

    Task IReminders.Start(string name, TimeSpan due, TimeSpan period)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        RefuseStateless();
        return ticks.StartReminder(name, due, period);
    }

    Task IReminders.Stop(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        RefuseStateless();
        return ticks.StopReminder(name);
    }

    public Task<IReadOnlyList<string>> Names()
    {
        RefuseStateless();
        return ticks.ReminderNames();
    }

    private void RefuseStateless()
    {
        if (registration.Policy == HostPolicy.Stateless)
        {
            throw new InvalidOperationException($"{registration.Class.FullName} is a [StatelessNeuron] class: it has many activations and cannot hold a reminder.");
        }
    }
}
