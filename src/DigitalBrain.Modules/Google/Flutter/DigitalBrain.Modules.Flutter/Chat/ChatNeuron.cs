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
        ArgumentNullException.ThrowIfNull(projection.Actions);
        if (projection.Actions.Length > 16 || projection.Actions.Any(action => action is null
            || string.IsNullOrWhiteSpace(action.Id) || action.Id.Length > 256
            || string.IsNullOrWhiteSpace(action.Label) || action.Label.Length > 256
            || action.Payload is null || action.Payload.Length > 32_000)
            || projection.Actions.Select(action => action.Id).Distinct(StringComparer.Ordinal).Count() != projection.Actions.Length)
        { throw new ArgumentException("Chat actions need unique stable IDs, labels, and bounded payloads."); }

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
            Projection = projection with
            {
                Messages = [.. projection.Messages.Select(message => message with { Activities = [.. message.Activities] })],
                Actions = [.. projection.Actions],
            },
        };
        try { await store.WriteStateAsync(); }
        catch { store.State = previous; throw; }
        await PublishAsync(new ChatChanged(this.GetPrimaryKeyString(), store.State.Revision));
    }

    public async Task Activate(string source, long generation, string actionId)
    {
        var projection = store.State.Projection;
        if (projection is null || projection.Source != source || projection.Generation != generation
            || (projection.Actions ?? []).FirstOrDefault(action => action.Id == actionId) is not { Enabled: true } action)
        { throw new InvalidOperationException("This chat action is no longer available."); }
        // Use the canonical payload. Streaming versions do not invalidate the same action identity.
        var activated = new ChatActionActivated(this.GetPrimaryKeyString(), source, generation,
            action.Id, action.Payload, Guid.NewGuid());
        await PublishAsync(activated);
        await GrainFactory.GetGrain<IUiBinding>(this.GetPrimaryKeyString()).Dispatch(activated);
    }

    public Task<ChatState> Read() => Task.FromResult(store.State.Projection is { Actions: null } legacy
        ? store.State with { Projection = legacy with { Actions = [] } }
        : store.State);
}
