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

        // The os signals become plain [Signal] records family by family (06-04..06-08); a wire record still pinned with its field ids
        // stands in, whichever it is.
        Assert.Contains(manifest.Contracts, pair => pair.Value.Contains("=0", StringComparison.Ordinal));
        Assert.Equal("Signal", manifest.Signals["IntoChat.OS.Signal"]);
        // The last grains named by Orleans' class-name convention (the AI media neurons) are plain since 06-05, their types frozen
        // by [Neuron("imagegeneratorneuron")] and pinned in NeuronGrainTypes.json; a grain sample stands in.
        Assert.Equal("ledger", manifest.Grains["IntoChat.OS.Authority.Compute.LedgerGrain"]);
        Assert.True(manifest.Contracts.Count > 500, $"{manifest.Contracts.Count} contracts");
        // Shrinks as grains become plain neurons (06-08: under 50); the floor only proves the capture found the grains.
        Assert.True(manifest.Grains.Count > 25, $"{manifest.Grains.Count} grains");
        // Shrinks as neurons leave Orleans storage (06-08: the Authority raw grains, 20); the floor only proves the capture found the
        // stateful grains.
        Assert.True(manifest.States.Count > 10, $"{manifest.States.Count} grains with state");
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
