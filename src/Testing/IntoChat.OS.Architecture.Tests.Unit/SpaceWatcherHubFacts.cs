using System.Text.RegularExpressions;

namespace IntoChat.OS.Architecture.Tests.Unit;

// 06-07b (R4-17): SpaceWatcher hears legacy-published signals through the local hub until their publishers migrate, and a
// signal moves to IBrainListeners in the same change as its publisher. SpaceWatcherLegacyHubSignals.json lists the hub
// subscriptions and only shrinks (06-04 Card/Person, 06-05 Workspace, 06-06a Apps/Circles); 06-08 deletes the hub. A plain
// [Signal] record is never taken from the hub.
public sealed partial class SpaceWatcherHubFacts
{
    internal const string FileName = "SpaceWatcherLegacyHubSignals.json";
    private const string Watcher = "IntoChat.OS.Modules.Flutter/Space/SpaceWatcher.cs";

    [GeneratedRegex(@"\bhub\.Subscribe<(\w+)>", RegexOptions.CultureInvariant)]
    private static partial Regex HubSubscription();

    private static string[] Subscribed()
        => [.. HubSubscription().Matches(File.ReadAllText(Repository.FromProjectKey(Watcher))).Select(match => match.Groups[1].Value).Distinct(StringComparer.Ordinal)];

    internal static Dictionary<string, int> Current() => Subscribed().ToDictionary(name => name, _ => 1, StringComparer.Ordinal);

    [Fact]
    public void LegacyHubSignalsOnlyShrink() => NeuronMigrationFacts.CountsOnlyShrink(FileName, Current(),
        "SpaceWatcher took a new signal from the local hub; listen through IBrainListeners");

    [GeneratedRegex(@"^\s*(?:using|namespace)\s+([\w.]+)\s*;", RegexOptions.CultureInvariant | RegexOptions.Multiline)]
    private static partial Regex NamespaceLine();

    [GeneratedRegex(@"^\s*namespace\s+([\w.]+)\s*;", RegexOptions.CultureInvariant | RegexOptions.Multiline)]
    private static partial Regex FileNamespace();

    // The namespaces SpaceWatcher's simple type names resolve in: its usings and its own (06-06a: a [Signal] record of the same
    // name in a namespace SpaceWatcher does not see, e.g. Roslyn's WorkspaceChanged, is another type).
    private static HashSet<string> WatcherNamespaces()
        => NamespaceLine().Matches(File.ReadAllText(Repository.FromProjectKey(Watcher))).Select(match => match.Groups[1].Value).ToHashSet(StringComparer.Ordinal);

    [Fact]
    public void NoPlainSignalIsTakenFromTheHub()
    {
        var seen = WatcherNamespaces();
        var plain = Repository.ProjectFiles("*.cs", path => !path.EndsWith(".g.cs", StringComparison.OrdinalIgnoreCase))
            .Select(File.ReadAllText)
            .Where(text => FileNamespace().Match(text) is { Success: true } declared && seen.Contains(declared.Groups[1].Value))
            .SelectMany(text => Subscribed().Where(name => Regex.IsMatch(text, @"\[(?:DigitalBrain\.)?Signal\(""[^""]*""\)\]\s*(?:public|internal)\s+(?:sealed\s+)?record\s+" + name + @"\b")))
            .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        Assert.True(plain.Length == 0, "A plain [Signal] is published to the runtime, not the hub; listen through IBrainListeners:\n" + string.Join('\n', plain));
    }

    [Fact(Explicit = true)]
    public void CaptureSpaceWatcherLegacyHubSignals() => CaptureWriter.Write(FileName, CaptureWriter.Sorted(FileName, Current().OrderBy(entry => entry.Key, StringComparer.Ordinal)));
}
