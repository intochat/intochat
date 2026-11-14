using DigitalBrain.Contracts;

namespace IntoChat.OS.Modules.Time.Reminders;

[NeuronAlias("reminder")]
[DefaultNeuronType("reminder")]
public interface IReminder
{
    /// <summary>
    /// Replace the persistent periodic registration. Missed ticks are not replayed.
    /// </summary>
    Task Start(TimeSpan dueTime, TimeSpan period);

    /// <summary>
    /// Remove the registration. A previously queued tick may still arrive.
    /// </summary>
    Task Stop();
}
