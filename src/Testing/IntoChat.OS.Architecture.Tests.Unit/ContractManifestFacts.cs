namespace IntoChat.OS.Architecture.Tests.Unit;

// R-B4 (clean-core W1, section 4.2): every grain type, alias, field id, state name and signal short name in
// ContractManifest.json is persisted or on the wire, so each must survive every later PR unchanged. Additions are
// allowed, as additive state already is; a rename, renumbering or removal fails. Additions stay unpinned until the
// baseline is recaptured, which TheBaselineRecordsEveryCurrentIdentity enforces. CaptureContractManifest rewrites
// the baseline when run explicitly (the test executable with --filter-method "*CaptureContractManifest" --explicit only).
public sealed class ContractManifestFacts
{
    [Fact]
    public void EveryRecordedIdentityIsUnchanged()
    {
        var baseline = ContractManifest.Load(ContractManifest.BaselinePath);
        var current = ContractManifest.Capture();

        var changes = Compare("Grains", baseline.Grains, current.Grains)
            .Concat(Compare("GrainInterfaces", baseline.GrainInterfaces, current.GrainInterfaces))
            .Concat(Compare("Contracts", baseline.Contracts, current.Contracts))
            .Concat(Compare("States", baseline.States, current.States))
            .Concat(Compare("Signals", baseline.Signals, current.Signals))
            .ToArray();

        Assert.True(changes.Length == 0,
            "Persisted or wire identities changed; stored brains and running clients still carry the old values. Restore them, "
            + "or coordinate a dual-read migration before rewriting ContractManifest.json:\n" + string.Join('\n', changes));
    }

    // Additions pass the fact above but stay unpinned until the baseline records them, so a stale baseline fails too:
    // a PR that adds a grain, contract, state or signal recaptures ContractManifest.json in the same PR.
    [Fact]
    public void TheBaselineRecordsEveryCurrentIdentity()
    {
        var baseline = ContractManifest.Load(ContractManifest.BaselinePath);
        var current = ContractManifest.Capture();

        var unpinned = Added("Grains", baseline.Grains, current.Grains)
            .Concat(Added("GrainInterfaces", baseline.GrainInterfaces, current.GrainInterfaces))
            .Concat(Added("Contracts", baseline.Contracts, current.Contracts))
            .Concat(Added("States", baseline.States, current.States))
            .Concat(Added("Signals", baseline.Signals, current.Signals))
            .ToArray();

        Assert.True(unpinned.Length == 0,
            "New identities are not pinned yet; run CaptureContractManifest and commit ContractManifest.json:\n" + string.Join('\n', unpinned));
    }

    // The capture finds what the W1 design counted, so the fact above cannot pass vacuously.
    [Fact]
    public void TheBaselineCoversTheKnownIdentities()
    {
        var manifest = ContractManifest.Load(ContractManifest.BaselinePath);

        // TimerTick (06-07) and TextChanged (06-04) became plain [Signal] records; a family 3 signal stands in until Circles migrates.
        Assert.StartsWith("circles.circle-changed; ", manifest.Contracts["IntoChat.OS.Modules.Circles.Signals.CircleChanged"], StringComparison.Ordinal);
        Assert.Equal("Signal", manifest.Signals["IntoChat.OS.Signal"]);
        // A neuron without [GrainType] gets its grain type from the class name; it is pinned all the same.
        Assert.Contains(manifest.Grains, grain => grain.Key.EndsWith(".ImageGeneratorNeuron", StringComparison.Ordinal));
        Assert.True(manifest.Contracts.Count > 500, $"{manifest.Contracts.Count} contracts");
        Assert.True(manifest.Grains.Count > 100, $"{manifest.Grains.Count} grains");
        Assert.True(manifest.States.Count > 50, $"{manifest.States.Count} grains with state");
    }

    [Fact(Explicit = true)]
    public void CaptureContractManifest()
        => File.WriteAllText(ContractManifest.BaselinePath, ContractManifest.Capture().ToJson());

    private static IEnumerable<string> Added(string section, SortedDictionary<string, string> baseline, SortedDictionary<string, string> current)
        => current.Where(entry => !baseline.ContainsKey(entry.Key)).Select(entry => $"{section} {entry.Key}: \"{entry.Value}\"");

    private static IEnumerable<string> Compare(string section, SortedDictionary<string, string> baseline, SortedDictionary<string, string> current)
    {
        foreach (var (key, value) in baseline)
        {
            if (!current.TryGetValue(key, out var now)) { yield return $"{section} {key}: removed (was \"{value}\")"; }
            else if (!string.Equals(value, now, StringComparison.Ordinal)) { yield return $"{section} {key}: \"{value}\" -> \"{now}\""; }
        }
    }
}
