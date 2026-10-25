using IntoChat.OS;
using IntoChat.OS.Modules.AI.Agents.Context.Signals;
using IntoChat.OS.Contracts.Configuration;
using IntoChat.OS.Framework;
using Orleans.Runtime;
using DigitalBrain.State;

namespace IntoChat.OS.Modules.AI.Agents.Context;

[GenerateSerializer, Alias("agent-context.state")]
[ExportableState]
public sealed record AgentContextState
{
    [Id(0)] public long Revision { get; init; }
    [Id(1)] public ContextEntry[] Entries { get; init; } = [];
}

[GrainType("agent-context")]
internal sealed class AgentContextNeuron(
    [PersistentState("agent-context", IntoChatOSNames.ClusterGrainStorage)] IPersistentState<AgentContextState> store)
    : Neuron<AgentContextState>(store), IAgentContext
{
    public async Task Set(ContextEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        if (string.IsNullOrWhiteSpace(entry.Name) || entry.Name.Length > 200) { throw new ArgumentException("A context entry needs a name of at most 200 characters.", nameof(entry)); }
        // Entries arrive as JSON from intochat-os-mcp and scripts, where a missing field is null.
        if (entry.Text is null || entry.Text.Length > 32000) { throw new ArgumentException("A context entry holds text of at most 32000 characters.", nameof(entry)); }
        if (entry.Contracts is null || entry.Contracts.Length > 128 || entry.Contracts.Any(string.IsNullOrWhiteSpace)) { throw new ArgumentException("A context entry names at most 128 non-empty contracts.", nameof(entry)); }
        var kept = entry with { Contracts = entry.Contracts.ToArray() };
        var index = Array.FindIndex(Snapshot.Entries, item => item.Name == entry.Name);
        ContextEntry[] entries = index < 0 ? [.. Snapshot.Entries, kept] : [.. Snapshot.Entries[..index], kept, .. Snapshot.Entries[(index + 1)..]];
        var revision = Snapshot.Revision + 1;
        await Save(Snapshot with { Revision = revision, Entries = entries }, new ContextUpdated(this.GetPrimaryKeyString(), revision, entry.Name));
    }

    public async Task Remove(string name)
    {
        var index = Array.FindIndex(Snapshot.Entries, item => item.Name == name);
        if (index < 0) { return; }
        var revision = Snapshot.Revision + 1;
        ContextEntry[] entries = [.. Snapshot.Entries[..index], .. Snapshot.Entries[(index + 1)..]];
        await Save(Snapshot with { Revision = revision, Entries = entries }, new ContextRemoved(this.GetPrimaryKeyString(), revision, name));
    }

    public Task<ContextSnapshot> Read() => Task.FromResult(new ContextSnapshot(Snapshot.Revision, Snapshot.Entries.ToArray()));
}
