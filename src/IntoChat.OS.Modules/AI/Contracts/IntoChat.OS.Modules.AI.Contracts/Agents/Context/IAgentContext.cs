using DigitalBrain;
using DigitalBrain.Contracts;
using IntoChat.OS;

namespace IntoChat.OS.Modules.AI.Agents.Context;

// What an agent should know before it answers, as named slices each producer owns. The agent reads
// the latest snapshot at turn time; behaviors subscribe to the signals.
[NeuronAlias("agent-context"), DefaultNeuronType("agent-context")]
public interface IAgentContext
{
    Task Set(ContextEntry entry);
    Task Remove(string name);
    [DigitalBrain.ReadOnlyCall, DigitalBrain.Interleaves] Task<ContextSnapshot> Read();
}

// Who wrote an entry and how far to trust it; the neuron stamps this from the capability caller, never from the payload.
public sealed record EntryProvenance(string Writer, DateTimeOffset At, string Trust);

// Text is what the model should know; Contracts are full type names of neuron contracts it may need for it.
// Tags route the entry to consumers; null means untagged (state persisted before tags existed).
public sealed record ContextEntry(string Name, string Text, string[] Contracts, string[]? Tags = null, EntryProvenance? Provenance = null)
{
    public string[] EffectiveTags => Tags ?? [];
}

public sealed record ContextSnapshot(long Revision, ContextEntry[] Entries);

public static class ContextTags
{
    public const string Inference = "inference", Interrupt = "interrupt", UiFocus = "ui.focus";
}

public static class ContextTrust
{
    public const string User = "user", App = "app", Model = "model", Platform = "platform";

    public static string Of(DigitalBrain.Capabilities.CapabilityCaller? caller) => caller?.Kind switch
    {
        DigitalBrain.Capabilities.CallerKind.User => User,
        DigitalBrain.Capabilities.CallerKind.Agent => Model,
        DigitalBrain.Capabilities.CallerKind.Platform or DigitalBrain.Capabilities.CallerKind.Scheduler => Platform,
        _ => App,
    };
}

public static class ContextKeys
{
    public static string Of(string brain, string name) => $"{brain}/context/{name}";
}
