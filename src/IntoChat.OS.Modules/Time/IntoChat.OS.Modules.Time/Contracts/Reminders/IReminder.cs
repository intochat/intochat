using IntoChat.OS;
namespace IntoChat.OS.Time.Reminders;

[Alias("reminder")]
[Orleans.Metadata.DefaultGrainType("reminder")]
public interface IReminder : INeuron
{
    // Replace the persistent periodic registration. Missed ticks are not replayed.
    Task Start(TimeSpan dueTime, TimeSpan period);
    // Remove the registration. A previously queued tick may still arrive.
    Task Stop();
}
