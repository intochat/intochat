namespace IntoChat.OS.Modules.Apps;

[DigitalBrain.Neuron("ino.conversations")]
public sealed class InoConversationsNeuron : DigitalBrain.Neuron<InoConversationsState>, IInoConversations
{
    private const int Kept = 64;

    public async Task Use(string agentKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(agentKey);
        if (State.Agents is [var current, ..] && current == agentKey) { return; }
        var agents = State.Agents.Where(key => key != agentKey).Prepend(agentKey).Take(Kept).ToArray();
        await Save(State with { Agents = agents }, new InoConversationsChanged(agentKey));
    }

    public Task<string[]> Current() => Task.FromResult<string[]>(State.Agents is [var current, ..] ? [current] : []);
}
