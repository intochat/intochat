namespace IntoChat.OS.Architecture.Tests.Unit;

// A neuron holds a lease on a source through Neuron.WatchRenewing, which owns the renewal period, the activation
// resync and the logged failure paths. Module code that reads ObserverRenewal is a hand-rolled copy of it.
public sealed class LeaseRenewalFacts
{
    [Fact]
    public void ModuleCodeRenewsLeasesOnlyThroughWatchRenewing()
    {
        var modules = Repository.ModulesRoot + Path.DirectorySeparatorChar;
        var copies = Repository.ProjectFiles("*.cs", path => !path.Contains($"{Path.DirectorySeparatorChar}tests{Path.DirectorySeparatorChar}", StringComparison.Ordinal),
                project => project.Directory.StartsWith(modules, StringComparison.OrdinalIgnoreCase)
                    && !project.Name.EndsWith(".Tests", StringComparison.Ordinal) && !project.Name.Contains(".Tests.", StringComparison.Ordinal))
            .Where(path => File.ReadAllText(path).Contains("ObserverRenewal", StringComparison.Ordinal))
            .Select(Repository.ProjectKey).Order().ToArray();
        Assert.True(copies.Length == 0,
            "Hand-rolled lease renewal in module source. Use Neuron.WatchRenewing." + Environment.NewLine + string.Join(Environment.NewLine, copies));
    }
}
