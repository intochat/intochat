using IntoChat.OS.Modules.AI.Agents.Context;

namespace IntoChat.OS.Modules.Apps.Flows;

// The trusted interrupt entries of the brain's contexts, folded from context signals. An entry counts when it is tagged
// interrupt and its provenance trust is user or platform; a model-written one never does. It is kept in memory and rebuilt on
// activation from the posture context and the contexts the current conversation agent lists.
internal sealed class SecurityInterrupts
{
    public const string CancelFlowsTag = "cancel-flows";

    private readonly Dictionary<(string Context, string Name), string> _active = [];

    // Deterministic when several are active: the first by context, then by name.
    public string? Reason => _active.Count == 0 ? null
        : _active.OrderBy(item => item.Key.Context, StringComparer.Ordinal).ThenBy(item => item.Key.Name, StringComparer.Ordinal).First().Value;

    public void Clear() => _active.Clear();

    public bool Remove(string contextId, string name) => _active.Remove((contextId, name));

    // Folds the entry now at the address (null when gone); true when it is a trusted interrupt that also asks to cancel Ino's flows.
    public bool Keep(string contextId, string name, ContextEntry? entry)
    {
        var at = (contextId, name);
        if (entry is null || !entry.EffectiveTags.Contains(ContextTags.Interrupt) || entry.Provenance?.Trust is not (ContextTrust.User or ContextTrust.Platform))
        {
            _active.Remove(at);
            return false;
        }
        _active[at] = entry.Text;
        return entry.EffectiveTags.Contains(CancelFlowsTag);
    }
}
