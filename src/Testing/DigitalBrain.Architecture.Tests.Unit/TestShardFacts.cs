using System.Text.Json;
using System.Text.RegularExpressions;

namespace DigitalBrain.Architecture.Tests.Unit;

// CI runs the PR gate as a matrix of shards, each a list of --test-modules globs in
// .github/test-shards.json. A glob that matches nothing is not an error to `dotnet test`, so a
// new or renamed test project would silently drop out of CI; this fact holds every PR-gate
// project to exactly one shard and keeps the nightly tiers out of all of them.
public sealed class TestShardFacts
{
    [Fact]
    public void EveryPullRequestGateProjectIsInExactlyOneShard()
    {
        var shards = Shards();
        string[] gate = [".Tests.Unit", ".Tests.Integration", ".Tests.Hosting"];
        string[] nightly = [".Tests.E2E", ".Tests.Browser"];
        var problems = new List<string>();
        foreach (var name in Repository.TestProjects().Select(project => project.Name).Order(StringComparer.Ordinal))
        {
            var owners = shards.Where(shard => shard.Value.Any(glob => glob.IsMatch(name + ".dll"))).Select(shard => shard.Key).ToArray();
            if (gate.Any(tier => name.EndsWith(tier, StringComparison.Ordinal)) && owners.Length != 1)
            {
                problems.Add($"{name} is in {owners.Length} shards ({string.Join(", ", owners)}); a PR-gate project needs exactly one");
            }
            if (nightly.Any(tier => name.EndsWith(tier, StringComparison.Ordinal)) && owners.Length != 0)
            {
                problems.Add($"{name} is nightly but sits in {string.Join(", ", owners)}");
            }
        }
        Assert.True(problems.Count == 0, "test-shards.json:\n" + string.Join('\n', problems));
    }

    private static Dictionary<string, Regex[]> Shards()
    {
        using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(Repository.Root, ".github", "test-shards.json")));
        return document.RootElement.GetProperty("shards").EnumerateObject().ToDictionary(
            shard => shard.Name,
            shard => shard.Value.EnumerateArray().Select(glob => FileNameGlob(glob.GetString()!)).ToArray(),
            StringComparer.Ordinal);
    }

    // The globs run against artifacts/bin; only the file-name segment decides membership.
    private static Regex FileNameGlob(string glob)
        => new("^" + Regex.Escape(glob[(glob.LastIndexOf('/') + 1)..]).Replace(@"\*", "[^/]*", StringComparison.Ordinal) + "$",
            RegexOptions.CultureInvariant);
}
