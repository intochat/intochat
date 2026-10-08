using DigitalBrain.Framework;
using Orleans.Runtime;

namespace DigitalBrain.Flutter.Chat;

[GrainType(UIVocabulary.ChatType)]
internal sealed class ChatNeuron(
    [PersistentState("state", DigitalBrainNames.DefaultGrainStorage)] IPersistentState<ChatState> store)
    : Neuron, IChat
{
    public async Task Project(ChatProjection projection)
    {
        ArgumentNullException.ThrowIfNull(projection);
        if (string.IsNullOrWhiteSpace(projection.Source) || projection.Source.Length > 512 || projection.Generation < 0 || projection.Version < 0)
        { throw new ArgumentException("A chat projection needs a source and non-negative generation and version."); }
        ArgumentNullException.ThrowIfNull(projection.Messages);
        if (projection.Messages.Length > 512 || projection.Messages.Any(message => message is null || string.IsNullOrWhiteSpace(message.Id)
            || message.Id.Length > 256 || !Enum.IsDefined(message.Role) || message.Text is null
            || message.Status is not ("running" or "completed" or "failed" or "cancelled" or "interrupted")
            || message.Activities is null || message.Activities.Any(activity => activity is null || string.IsNullOrWhiteSpace(activity.Id)
                || string.IsNullOrWhiteSpace(activity.Title) || activity.Input is null)
            || message.Activities.Select(activity => activity.Id).Distinct(StringComparer.Ordinal).Count() != message.Activities.Length)
            || projection.Messages.Select(message => message.Id).Distinct(StringComparer.Ordinal).Count() != projection.Messages.Length)
        { throw new ArgumentException("Chat messages and activities need unique stable IDs and valid content."); }

        if (store.State.Projection is { } current)
        {
            if (projection.Generation < current.Generation) { return; }
            if (projection.Generation == current.Generation)
            {
                if (projection.Source != current.Source) { throw new InvalidOperationException("A new chat source needs a new generation."); }
                if (projection.Version <= current.Version) { return; }
            }
        }
        var previous = store.State;
        store.State = new()
        {
            Name = this.GetPrimaryKeyString(),
            Revision = checked(previous.Revision + 1),
            Projection = projection with { Messages = [.. projection.Messages.Select(message => message with { Activities = [.. message.Activities] })] },
        };
        try { await store.WriteStateAsync(); }
        catch { store.State = previous; throw; }
        await PublishAsync(new ChatChanged(this.GetPrimaryKeyString(), store.State.Revision));
    }

    public Task<ChatState> Read() => Task.FromResult(store.State);
}
