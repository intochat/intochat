using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace IntoChat.OS.Architecture.Tests.Unit;

// CI runs the PR gate as a matrix of shards (unit, integration-a..d). Membership is by convention from
// .github/test-shards.json: tier globs pick the projects, md5(project name) % buckets spreads a tier over its
// shards, and pins override the bucket for projects that must not share a shard. ci.yml computes the same
// bucket in bash; the golden table below is asserted here and echoed by CI so the two cannot drift.
public sealed class TestShardFacts
{
    private static readonly (string Project, int Bucket)[] Golden =
    [
        ("IntoChat.OS.Kernel.Tests.Integration", 1),
        ("IntoChat.OS.Modules.Time.Tests.Integration", 2),
        ("IntoChat.OS.Modules.Postgres.Tests.Integration", 3),
        ("IntoChat.OS.Modules.ArtCraft.Tests.Integration", 0),
        ("IntoChat.OS.Modules.Apps.Tests.Integration", 1),
        ("IntoChat.OS.Client.Orleans.Tests.Integration", 3),
    ];

    // Big-endian uint32 of the first four md5 bytes, modulo the bucket count (bash: 16#<first 8 hex> % n).
    internal static int Bucket(string project, int buckets)
        => (int)(System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(MD5.HashData(Encoding.UTF8.GetBytes(project))) % (uint)buckets);

    [Fact]
    public void EveryPullRequestGateProjectIsInExactlyOneShard()
    {
        var config = Config();
        string[] nightly = [".Tests.E2E", ".Tests.Browser"];
        var problems = new List<string>();
        foreach (var name in Repository.TestProjects().Select(project => project.Name).Order(StringComparer.Ordinal))
        {
            var shards = config.ShardsOf(name);
            var tiers = config.Tiers.Where(tier => tier.Value.Any(glob => glob.IsMatch(name + ".dll"))).Select(tier => tier.Key).ToArray();
            if (tiers.Length > 1) { problems.Add($"{name} matches {tiers.Length} tiers ({string.Join(", ", tiers)})"); }
            if (nightly.Any(tier => name.EndsWith(tier, StringComparison.Ordinal)) && shards.Length != 0)
            {
                problems.Add($"{name} is nightly but sits in {string.Join(", ", shards)}");
            }
            if (name.EndsWith(".Tests.Unit", StringComparison.Ordinal) || name.EndsWith(".Tests.Integration", StringComparison.Ordinal)
                || name.EndsWith(".Tests.Hosting", StringComparison.Ordinal))
            {
                if (shards.Length != 1) { problems.Add($"{name} is in {shards.Length} shards; a PR-gate project needs exactly one"); }
            }
        }
        Assert.True(problems.Count == 0, "test-shards.json:\n" + string.Join('\n', problems));
    }

    [Fact]
    public void TheBucketFunctionMatchesTheGoldenTableCiEchoes()
    {
        var ci = File.ReadAllText(Path.Combine(Repository.Root, ".github", "workflows", "ci.yml"));
        var problems = new List<string>();
        foreach (var (project, bucket) in Golden)
        {
            if (Bucket(project, 4) != bucket) { problems.Add($"{project}: C# bucket {Bucket(project, 4)}, golden {bucket}"); }
            if (!ci.Contains($"{project}={bucket}", StringComparison.Ordinal)) { problems.Add($"ci.yml golden list lacks {project}={bucket}"); }
        }
        Assert.True(problems.Count == 0, string.Join('\n', problems));
    }

    [Fact]
    public void EveryPinNamesAnExistingProjectAndAValidBucket()
    {
        var config = Config();
        var names = Repository.TestProjects().Select(project => project.Name).ToHashSet(StringComparer.Ordinal);
        var problems = new List<string>();
        foreach (var (project, bucket) in config.Pins)
        {
            if (!names.Contains(project)) { problems.Add($"pin {project} is not a test project"); continue; }
            var tier = config.Tiers.Single(tier => tier.Value.Any(glob => glob.IsMatch(project + ".dll"))).Key;
            if (bucket < 0 || bucket >= config.Buckets[tier]) { problems.Add($"pin {project}={bucket} is outside 0..{config.Buckets[tier] - 1}"); }
        }
        Assert.True(problems.Count == 0, string.Join('\n', problems));
    }

    private static ShardConfig Config()
    {
        using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(Repository.Root, ".github", "test-shards.json")));
        var root = document.RootElement;
        return new ShardConfig(
            root.GetProperty("tiers").EnumerateObject().ToDictionary(
                tier => tier.Name, tier => tier.Value.EnumerateArray().Select(glob => FileNameGlob(glob.GetString()!)).ToArray(), StringComparer.Ordinal),
            root.GetProperty("buckets").EnumerateObject().ToDictionary(tier => tier.Name, tier => tier.Value.GetInt32(), StringComparer.Ordinal),
            root.GetProperty("pins").EnumerateObject().ToDictionary(pin => pin.Name, pin => pin.Value.GetInt32(), StringComparer.Ordinal));
    }

    private sealed record ShardConfig(Dictionary<string, Regex[]> Tiers, Dictionary<string, int> Buckets, Dictionary<string, int> Pins)
    {
        // unit -> "unit"; integration bucket i -> integration-a..d. A project outside every tier has no shard.
        internal string[] ShardsOf(string project)
        {
            foreach (var (tier, globs) in Tiers)
            {
                if (!globs.Any(glob => glob.IsMatch(project + ".dll"))) { continue; }
                var count = Buckets[tier];
                if (count == 1) { return [tier]; }
                var bucket = Pins.TryGetValue(project, out var pinned) ? pinned : Bucket(project, count);
                return [tier + "-" + (char)('a' + bucket)];
            }
            return [];
        }
    }

    // The globs run against artifacts/bin; only the file-name segment decides membership.
    private static Regex FileNameGlob(string glob)
        => new("^" + Regex.Escape(glob[(glob.LastIndexOf('/') + 1)..]).Replace(@"\*", "[^/]*", StringComparison.Ordinal) + "$",
            RegexOptions.CultureInvariant);
}
