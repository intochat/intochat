using System.Text.Json;
using Microsoft.Agents.AI.Workflows;
using Microsoft.Agents.AI.Workflows.Checkpointing;

namespace IntoChat.OS.Modules.AI.Workflows;

// Agent Framework workflow checkpoints kept in a neuron's own state: each checkpoint is one JSON text under "{session}/{id}", so a
// workflow that waits (for an approval, for example) survives a restart with the brain's durability and no second store.
internal sealed class StateCheckpointStore(IDictionary<string, string> checkpoints) : JsonCheckpointStore
{
    public override ValueTask<CheckpointInfo> CreateCheckpointAsync(string sessionId, JsonElement value, CheckpointInfo? parent = null)
    {
        var info = new CheckpointInfo(sessionId, $"{checkpoints.Count(entry => entry.Key.StartsWith(sessionId + "/", StringComparison.Ordinal)) + 1:D6}");
        checkpoints[Key(info)] = value.GetRawText();
        return ValueTask.FromResult(info);
    }

    public override ValueTask<JsonElement> RetrieveCheckpointAsync(string sessionId, CheckpointInfo key)
        => checkpoints.TryGetValue(Key(key), out var json)
            ? ValueTask.FromResult(JsonDocument.Parse(json).RootElement.Clone())
            : throw new KeyNotFoundException($"No checkpoint {key.CheckpointId} for {sessionId}.");

    public override ValueTask<IEnumerable<CheckpointInfo>> RetrieveIndexAsync(string sessionId, CheckpointInfo? withParent = null)
        => ValueTask.FromResult<IEnumerable<CheckpointInfo>>([.. checkpoints.Keys
            .Where(key => key.StartsWith(sessionId + "/", StringComparison.Ordinal)).Order(StringComparer.Ordinal)
            .Select(key => new CheckpointInfo(sessionId, key[(sessionId.Length + 1)..]))]);

    private static string Key(CheckpointInfo info) => $"{info.SessionId}/{info.CheckpointId}";
}
