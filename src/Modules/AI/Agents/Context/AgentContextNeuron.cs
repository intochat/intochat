using DigitalBrain;
using DigitalBrain.Neurons;
using DigitalBrain.Runtime.Orleans.Capabilities;
using DigitalBrain.State;
using IntoChat.OS;
using IntoChat.OS.Modules.AI.Agents.Context.Rules;
using IntoChat.OS.Modules.AI.Agents.Context.Signals;
using DigitalBrain.Access;
using IntoChat.OS.Host.Configuration;

namespace IntoChat.OS.Modules.AI.Agents.Context;

[GenerateSerializer, Alias("agent-context.state")]
[ExportableState]
internal sealed record AgentContextState
{
    [Id(0)] public long Revision { get; init; }
    [Id(1)] public ContextEntry[] Entries { get; init; } = [];
}

[Neuron("agent-context", State = "agent-context")]
internal sealed class AgentContextNeuron
    : AiNeuron<AgentContextState>, IAgentContext
{
    public async Task Set(ContextEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        if (string.IsNullOrWhiteSpace(entry.Name) || entry.Name.Length > 200) { throw new ArgumentException("A context entry needs a name of at most 200 characters.", nameof(entry)); }
        // Entries arrive as JSON from intochat-os-mcp and scripts, where a missing field is null.
        if (entry.Text is null || entry.Text.Length > 32000) { throw new ArgumentException("A context entry holds text of at most 32000 characters.", nameof(entry)); }
        if (entry.Contracts is null || entry.Contracts.Length > 128 || entry.Contracts.Any(string.IsNullOrWhiteSpace)) { throw new ArgumentException("A context entry names at most 128 non-empty contracts.", nameof(entry)); }
        var tags = entry.Tags;
        if (tags is not null && (tags.Length > 8 || tags.Any(string.IsNullOrWhiteSpace))) { throw new ArgumentException("A context entry carries at most 8 non-empty tags.", nameof(entry)); }
        var caller = Context.Caller;
        var trust = ContextTrust.Of(caller);
        await Check("Set", HeldInterrupt(entry.Name, caller));
        var provenance = new EntryProvenance(CallerRules.IsApp(caller, out var app) ? app : CallerRules.ActsFor(caller) ?? "", Context.Time.GetUtcNow(), trust);
        var kept = entry with { Contracts = entry.Contracts.ToArray(), Tags = tags?.ToArray(), Provenance = provenance };
        // An interrupt is the user's or the platform's to raise; a model or app still gets its entry stored, just not acted on.
        var ignored = kept.EffectiveTags.Contains(ContextTags.Interrupt) && trust is not (ContextTrust.User or ContextTrust.Platform);
        var index = Array.FindIndex(Snapshot.Entries, item => item.Name == entry.Name);
        ContextEntry[] entries = index < 0 ? [.. Snapshot.Entries, kept] : [.. Snapshot.Entries[..index], kept, .. Snapshot.Entries[(index + 1)..]];
        var revision = Snapshot.Revision + 1;
        await Save(Snapshot with { Revision = revision, Entries = entries }, new ContextUpdated(FullKey, revision, entry.Name, ignored));
    }

    public async Task Remove(string name)
    {
        var index = Array.FindIndex(Snapshot.Entries, item => item.Name == name);
        if (index < 0) { return; }
        await Check("Remove", HeldInterrupt(name, Context.Caller));
        var revision = Snapshot.Revision + 1;
        ContextEntry[] entries = [.. Snapshot.Entries[..index], .. Snapshot.Entries[(index + 1)..]];
        await Save(Snapshot with { Revision = revision, Entries = entries }, new ContextRemoved(FullKey, revision, name));
    }

    public async Task<bool> Upsert(ContextEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        // The same rule as Set, applied as for an app whatever the caller: a producer's entry never clears an interrupt.
        if (new InterruptClearedByTrust(HeldEntry(entry.Name), ContextTrust.App).Broken) { return false; }
        await Set(entry);
        return true;
    }

    public async Task<bool> Withdraw(string name)
    {
        if (new InterruptClearedByTrust(HeldEntry(name), ContextTrust.App).Broken) { return false; }
        await Remove(name);
        return true;
    }

    private ContextEntry? HeldEntry(string name)
        => Snapshot.Entries.FirstOrDefault(item => item.Name == name);

    // Refused for a model or an app caller, and for a call with no caller (which reads as app).
    private InterruptClearedByTrust HeldInterrupt(string name, DigitalBrain.Access.Caller? caller)
        => new(HeldEntry(name), ContextTrust.Of(caller));

    public Task<ContextSnapshot> Read() => Task.FromResult(new ContextSnapshot(Snapshot.Revision, Snapshot.Entries.ToArray()));
}
