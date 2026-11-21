namespace IntoChat.OS.Architecture.Tests.Unit;

// Every module keeps its pure logic under a fast Unit tier (TEST-03). A module without one is either
// listed in Exempt with the reason it has no pure logic, or the build of this fact fails; an exemption
// whose module has since gained a unit tier is stale and fails too, so the list only ever shrinks.
public sealed class ModuleUnitTierFacts
{
    private static readonly Dictionary<string, string> Exempt = new(StringComparer.Ordinal);

    [Fact]
    public void EveryModuleHasAUnitTierOrIsExempt()
    {
        var missing = Modules().Where(module => !Exempt.ContainsKey(module) && !HasUnitTier(module)).Order(StringComparer.Ordinal).ToArray();
        Assert.True(missing.Length == 0, "modules without a *.Tests.Unit project (add one, or exempt with a reason):\n" + string.Join('\n', missing));
    }

    [Fact]
    public void ExemptionsNameModulesThatStillLackAUnitTier()
    {
        var modules = Modules().ToHashSet(StringComparer.Ordinal);
        var stale = Exempt.Keys.Where(name => !modules.Contains(name) || HasUnitTier(name)).Order(StringComparer.Ordinal).ToArray();
        Assert.True(stale.Length == 0, "stale unit-tier exemptions (module gone or now has a unit tier):\n" + string.Join('\n', stale));
    }

    private static bool HasUnitTier(string module)
        => Directory.Exists(Path.Combine(Repository.Root, "tests", "IntoChat.OS.Tests.Unit", "Modules", module))
            || Repository.Projects.ContainsKey($"IntoChat.OS.Modules.{module}.Tests.Unit");

    // Module main projects: IntoChat.OS.Modules.<Name>, not contracts, hosting, sandbox, shared fixtures or tests.
    private static IEnumerable<string> Modules()
        => Repository.Projects.Keys
            .Where(name => name.StartsWith("IntoChat.OS.Modules.", StringComparison.Ordinal)
                && !name.Contains(".Tests", StringComparison.Ordinal)
                && !name.EndsWith(".Contracts", StringComparison.Ordinal)
                && !name.EndsWith(".Generators", StringComparison.Ordinal)
                && !name.EndsWith(".Testing", StringComparison.Ordinal)
                && !name.EndsWith(".Aspire.Hosting", StringComparison.Ordinal)
                && !name.EndsWith(".Sandbox", StringComparison.Ordinal))
            .Select(name => name["IntoChat.OS.Modules.".Length..]);
}
