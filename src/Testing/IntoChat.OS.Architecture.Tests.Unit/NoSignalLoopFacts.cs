using System.Text.RegularExpressions;

namespace IntoChat.OS.Architecture.Tests.Unit;

// Phase 6 closure (06-09, success criterion 3): production code reacts to signals with [On] reactions and synapses. No source
// outside the test projects loops `await foreach` over On<T> unless SignalLoopAllowList.json names it (the client/session
// brain.On API and the prompt text that teaches it), nothing uses the deleted observer path, and no [Neuron] or [App] class
// declares the legacy Watch/Unwatch members or a signal-page Read (R4-03: plain neurons never implement them). Strict: the
// test projects keep their own shrink-only ratchet (ObserverPathOwners.json), since a fact may drive the client API.
public sealed partial class NoSignalLoopFacts
{
    [GeneratedRegex(@"\b(?:Task|ValueTask)\b[^\n;=]*\s(?:Watch|Unwatch)\s*\(|\bSignalPage\b[^\n;=]*\sRead\s*\(", RegexOptions.CultureInvariant)]
    private static partial Regex LegacyMember();

    [GeneratedRegex(@"\[\s*(?:DigitalBrain\.)?(?:Neuron|App)\s*\(", RegexOptions.CultureInvariant)]
    private static partial Regex NeuronOrApp();

    private static IEnumerable<(string Key, string Text)> Production()
        => Repository.ProjectFiles("*.cs", path => !path.EndsWith(".g.cs", StringComparison.OrdinalIgnoreCase),
                project => !project.Name.Contains(".Tests", StringComparison.Ordinal))
            .Select(path => (Repository.ProjectKey(path), File.ReadAllText(path)));

    [Fact]
    public void NoSignalLoops()
    {
        var allowed = ObserverPathFacts.AllowList().Select(entry => entry.File).ToHashSet(StringComparer.Ordinal);
        var offenders = Production()
            .Where(file => ObserverPathFacts.ObserverPath().IsMatch(file.Text) || (!allowed.Contains(file.Key) && ObserverPathFacts.SignalLoop().IsMatch(file.Text)))
            .Select(file => file.Key).Order(StringComparer.Ordinal).ToArray();
        Assert.True(offenders.Length == 0,
            "Signal loops or legacy observer use in production code; write an [On] reaction or a synapse (or name client/session API and prompt text in SignalLoopAllowList.json):\n"
            + string.Join('\n', offenders));
    }

    [Fact]
    public void NeuronsAndAppsDeclareNoLegacyWatchMembers()
    {
        var offenders = Production()
            .Where(file => NeuronOrApp().IsMatch(file.Text) && LegacyMember().IsMatch(file.Text))
            .Select(file => file.Key).Order(StringComparer.Ordinal).ToArray();
        Assert.True(offenders.Length == 0,
            "[Neuron]/[App] sources declare Watch, Unwatch or a signal-page Read; a subscriber opens a synapse on the runtime (R4-03):\n"
            + string.Join('\n', offenders));
    }
}
