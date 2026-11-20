using DigitalBrain.Brains;
using IntoChat.OS.Host.Primitives;

namespace IntoChat.OS.Modules.Apps;

[DigitalBrain.Neurons.Neuron("ino.conversations")]
internal sealed class InoConversationsNeuron : DigitalBrain.Neurons.Neuron<InoConversationsState>, IInoConversations
{
    private const int Kept = 64;
    public const string AgentKeyPrefix = "ino/conversations/";

    public string BrainId
    {
        get
        {
            var brain = Id.Brain.Value;
            if (string.IsNullOrWhiteSpace(brain) || !BrainScope.IsValidId(brain))
            {
                throw new InvalidOperationException($"Invalid brain id '{brain}'.");
            }
            return brain;
        }
    }

    public async Task Use(string agentKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(agentKey);
        var trimmed = agentKey.Trim();

        string keyToUse;
        if (trimmed.StartsWith(AgentKeyPrefix, StringComparison.Ordinal))
        {
            var raw = trimmed[AgentKeyPrefix.Length..];
            if (string.Equals(raw, "new", StringComparison.OrdinalIgnoreCase))
            {
                keyToUse = AgentKeyPrefix + ConversationId.New().Value;
            }
            else
            {
                if (!ConversationId.TryParse(raw, out var parsed))
                {
                    throw new ArgumentException($"Invalid conversation key '{agentKey}'.", nameof(agentKey));
                }
                keyToUse = AgentKeyPrefix + parsed.Value;
            }
        }
        else if (string.Equals(trimmed, "new", StringComparison.OrdinalIgnoreCase))
        {
            keyToUse = AgentKeyPrefix + ConversationId.New().Value;
        }
        else
        {
            if (!ConversationId.TryParse(trimmed, out var parsed))
            {
                throw new ArgumentException($"Invalid conversation key '{agentKey}'.", nameof(agentKey));
            }
            keyToUse = AgentKeyPrefix + parsed.Value;
        }

        if (State.Agents is [var current, ..] && current == keyToUse) { return; }
        var agents = State.Agents.Where(key => key != keyToUse).Prepend(keyToUse).Take(Kept).ToArray();
        await Save(State with { Agents = agents }, new InoConversationsChanged(keyToUse));
    }

    public Task<string[]> Current() => Task.FromResult<string[]>(State.Agents is [var current, ..] ? [current] : []);

    public Task<string[]> List() => Task.FromResult(State.Agents);

    public async Task<string> CreateNew()
    {
        var id = ConversationId.New();
        var agentKey = AgentKeyPrefix + id.Value;
        await Use(agentKey);
        return agentKey;
    }

    public Task<ConversationId?> CurrentConversationId()
    {
        if (State.Agents is [var current, ..])
        {
            var raw = current.StartsWith(AgentKeyPrefix, StringComparison.Ordinal)
                ? current[AgentKeyPrefix.Length..]
                : current;
            if (ConversationId.TryParse(raw, out var id))
            {
                return Task.FromResult<ConversationId?>(id);
            }
        }
        return Task.FromResult<ConversationId?>(null);
    }
}
