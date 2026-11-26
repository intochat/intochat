using DigitalBrain.Units;

namespace IntoChat.OS.Modules.Apps.Tests.Unit.Ino;

public sealed class SkillDiscoveryFacts
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string LocateWorkspaceAppsRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            var candidate = Path.Combine(current.FullName, "apps");
            if (Directory.Exists(candidate) && Directory.Exists(Path.Combine(candidate, "helloworld")))
            {
                return candidate;
            }
            current = current.Parent;
        }
        throw new DirectoryNotFoundException("Could not locate workspace apps directory.");
    }

    [Fact]
    public async Task DiscoversSkillsFromWorkspaceAppsAndDeployedUnitSidecars()
    {
        var appsRoot = LocateWorkspaceAppsRoot();
        var studio = new FakeStudioWithSidecars(new Dictionary<string, Dictionary<string, string>>(StringComparer.Ordinal)
        {
            ["custom.counter"] = new(StringComparer.Ordinal)
            {
                ["SKILL.md"] = """
                    ---
                    name: custom.counter
                    title: Custom Counter
                    description: Counts live button clicks inside a deployed Synapse unit.
                    capabilities: [view]
                    schedule: none
                    ---

                    # Custom Counter
                    Click increment to bump the counter.
                    """,
            },
        });

        var discovered = await SkillDiscovery.DiscoverAsync(studio, appsRoot, Ct);

        Assert.Contains(discovered, s => s.Alias == "custom.counter" && s.Source == "deployed-unit" && s.Title == "Custom Counter");
        Assert.Contains(discovered, s => s.Alias == "helloworld" && s.Source == "apps-folder" && s.Description.Contains("Hello, IntoChat OS!", StringComparison.Ordinal));
        Assert.Contains(discovered, s => s.Alias == "btcalert" && s.Source == "apps-folder"
            && s.Capabilities.Any(c => c.StartsWith("http", StringComparison.Ordinal))
            && s.Capabilities.Any(c => c.StartsWith("notifications", StringComparison.Ordinal)));
    }

    private sealed class FakeStudioWithSidecars(Dictionary<string, Dictionary<string, string>> sidecars) : ISynapseStudio
    {
        public Task<InoToolResult> Check(string source) => Task.FromResult(new InoToolResult("ok"));
        public Task<InoToolResult> Deploy(string alias, string source) => Task.FromResult(new InoToolResult("ok"));
        public Task<UnitDeployResult> DeployFiles(string alias, IReadOnlyDictionary<string, string> files, IReadOnlyDictionary<string, string> bindings)
            => throw new NotSupportedException();
        public Task<InoToolResult> Retire(string alias) => Task.FromResult(new InoToolResult("ok"));
        public Task<DeployedSource[]> List()
            => Task.FromResult(sidecars.Keys.Select(a => new DeployedSource(a, "h1", [a], DeployStatus.Deployed, null, DateTimeOffset.UtcNow, null)).ToArray());
        public Task<Dictionary<string, string>> Sidecars(string alias)
            => Task.FromResult(sidecars.TryGetValue(alias, out var s) ? new Dictionary<string, string>(s) : []);
        public Task<Dictionary<string, string>> SourceFiles(string alias) => Task.FromResult(new Dictionary<string, string>());
        public Task<InoToolResult> Failures(string alias) => Task.FromResult(new InoToolResult("none"));
    }
}
