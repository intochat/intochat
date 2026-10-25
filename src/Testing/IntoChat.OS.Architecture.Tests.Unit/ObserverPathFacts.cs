using System.Text.Json;
using System.Text.RegularExpressions;

namespace IntoChat.OS.Architecture.Tests.Unit;

// The legacy signalling path (observer leases, the local hub and its nudges, Watch/Unwatch, former 04 D-10) and signal
// loops written as `await foreach` over On<T> leave os family by family (R4-03, R4-04): a family PR moves its loops to
// [On] reactions or synapses and shrinks this file; 06-08 drives production to zero, 06-09 the tests.
// The client/session On API and prompt text that teaches it are legitimate loops, named in SignalLoopAllowList.json.
public sealed partial class ObserverPathFacts
{
    internal const string FileName = "ObserverPathOwners.json";
    internal const string AllowListFile = "SignalLoopAllowList.json";

    internal sealed record AllowedLoop(string File, string Kind, string Reason);

    [GeneratedRegex(@"WatchRenewing\(|\bObserverManager\b|\bINeuronObserver\b|\bI?LocalSignalHub\b|\bSignalNudge\b|\.Watch\(|\.Unwatch\(", RegexOptions.CultureInvariant)]
    private static partial Regex ObserverPath();

    [GeneratedRegex(@"await\s+foreach\s*\([^\n]*?\.On\s*[<(]", RegexOptions.CultureInvariant)]
    internal static partial Regex SignalLoop();

    internal static AllowedLoop[] AllowList()
        => JsonSerializer.Deserialize<AllowedLoop[]>(File.ReadAllText(CaptureWriter.Path(AllowListFile)))!;

    private static IEnumerable<(string Key, string Text)> Sources()
        => Repository.ProjectFiles("*.cs", path => !path.EndsWith(".g.cs", StringComparison.OrdinalIgnoreCase))
            .Select(path => (Repository.ProjectKey(path), File.ReadAllText(path)));

    internal static Dictionary<string, int> Current()
    {
        var allowed = AllowList().Select(entry => entry.File).ToHashSet(StringComparer.Ordinal);
        return Sources()
            .Select(file => (file.Key, Count: ObserverPath().Count(file.Text) + (allowed.Contains(file.Key) ? 0 : SignalLoop().Count(file.Text))))
            .Where(file => file.Count > 0)
            .ToDictionary(file => file.Key, file => file.Count, StringComparer.Ordinal);
    }

    [Fact]
    public void ObserverPathOnlyShrinks() => NeuronMigrationFacts.CountsOnlyShrink(FileName, Current(),
        "New legacy signalling (Watch, observers, the local hub) or a signal loop; write an [On] reaction or a synapse");

    // Every allow-listed file still holds a signal loop, and the list names only the two legitimate kinds.
    [Fact]
    public void SignalLoopAllowListHasNoStaleEntries()
    {
        var files = Sources().ToDictionary(file => file.Key, file => file.Text, StringComparer.Ordinal);
        foreach (var entry in AllowList())
        {
            Assert.True(entry.Kind is "client-session-api" or "prompt-text", $"{entry.File}: unknown kind {entry.Kind}.");
            Assert.False(string.IsNullOrWhiteSpace(entry.Reason), $"{entry.File}: no reason.");
            Assert.True(files.TryGetValue(entry.File, out var text) && SignalLoop().IsMatch(text),
                $"{entry.File} no longer holds a signal loop; drop it from {AllowListFile}.");
        }
    }

    [Fact(Explicit = true)]
    public void CaptureObserverPathOwners() => CaptureWriter.Write(FileName, CaptureWriter.Sorted(FileName, Current().OrderBy(entry => entry.Key, StringComparer.Ordinal)));
}
