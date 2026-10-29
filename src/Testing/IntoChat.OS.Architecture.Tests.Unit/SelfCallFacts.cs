using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using Orleans.Concurrency;

namespace IntoChat.OS.Architecture.Tests.Unit;

// A neuron's turn is non-reentrant: awaiting a call to its own reference from inside a turn waits for the turn
// that is awaiting it. A self-call is safe when its target is [OneWay] (sent, not awaited) or [AlwaysInterleave],
// or the neuron is [Reentrant]; the one-way hand-off is Neuron.Defer. Anything else needs a reasoned owner entry,
// and an owner entry that no longer matches a site fails (two-sided).
// Known false negative: `var self = AsReference<IX>(); await self.M()` is not seen by the scan.
public sealed partial class SelfCallFacts
{
    private sealed record Owner(string File, string Interface, string Method, string Reason);

    private sealed record Site(string File, string Class, string Interface, string Method);

    [GeneratedRegex(@"(?:await|return)\s+(?:this\.)?AsReference<(?<i>\w+)>\(\)\s*\.\s*(?<m>\w+)", RegexOptions.CultureInvariant)]
    private static partial Regex Awaited();

    [GeneratedRegex(@"Self<(?<i>\w+)>\(\)\s*\.\s*(?<m>\w+)", RegexOptions.CultureInvariant)]
    private static partial Regex Self();

    // A plain neuron (DigitalBrain) reaches itself through its own id.
    [GeneratedRegex(@"(?:await|return)\s+Get<(?<i>\w+)>\(Id\)\s*\.\s*(?<m>\w+)", RegexOptions.CultureInvariant)]
    private static partial Regex PlainSelf();

    [GeneratedRegex(@"\bclass\s+(?<c>\w+)", RegexOptions.CultureInvariant)]
    private static partial Regex Class();

    private static IEnumerable<Site> Sites()
    {
        var sep = Path.DirectorySeparatorChar;
        return Repository.ProjectFiles("*.cs", path => !path.Contains($"{sep}tests{sep}", StringComparison.Ordinal),
                project => !project.Name.EndsWith(".Tests", StringComparison.Ordinal) && !project.Name.Contains(".Tests.", StringComparison.Ordinal))
            .SelectMany(path =>
            {
                var text = File.ReadAllText(path);
                var key = Repository.ProjectKey(path);
                return Awaited().Matches(text).Concat(Self().Matches(text)).Concat(PlainSelf().Matches(text)).Select(hit =>
                {
                    var owner = Class().Matches(text[..hit.Index]).LastOrDefault()?.Groups["c"].Value ?? "";
                    return new Site(key, owner, hit.Groups["i"].Value, hit.Groups["m"].Value);
                });
            })
            .Distinct();
    }

    private static MethodInfo[] Targets(Site site)
    {
        var interfaces = ProductionArchitecture.Types.Where(type => type.IsInterface && type.Name == site.Interface).ToArray();
        Assert.True(interfaces.Length == 1, $"{site.File}: self-call target {site.Interface} resolves to {interfaces.Length} interfaces.");
        return [.. interfaces[0].GetMethods().Where(method => method.Name == site.Method)];
    }

    private static bool Safe(Site site)
    {
        var methods = Targets(site);
        Assert.True(methods.Length > 0, $"{site.File}: {site.Interface} has no method {site.Method}.");
        var reentrant = ProductionArchitecture.Types.Any(type => type.Name == site.Class
            && (type.IsDefined(typeof(ReentrantAttribute), false) || type.IsDefined(typeof(DigitalBrain.ReentrantAttribute), false)));
        return reentrant || methods.All(method => method.IsDefined(typeof(OneWayAttribute), false)
            || method.IsDefined(typeof(AlwaysInterleaveAttribute), false) || method.IsDefined(typeof(IntoChat.OS.OneWayCallAttribute), false)
            || method.IsDefined(typeof(DigitalBrain.OneWayCallAttribute), false) || method.IsDefined(typeof(DigitalBrain.InterleavesAttribute), false));
    }

    private static Owner[] Owners() => JsonSerializer.Deserialize<Owner[]>(
        File.ReadAllText(Path.Combine(Repository.ProjectNamed("IntoChat.OS.Architecture.Tests.Unit").Directory, "SelfCallOwners.json")),
        new JsonSerializerOptions(JsonSerializerDefaults.Web))!;

    [Fact]
    public void AnAwaitedSelfCallTargetsAOneWayOrInterleavingMethodOrIsAnOwner()
    {
        var owners = Owners();
        var intruders = Sites().Where(site => !Safe(site))
            .Where(site => !owners.Any(owner => owner.File == site.File && owner.Interface == site.Interface && owner.Method == site.Method))
            .Select(site => $"{site.File}: {site.Interface}.{site.Method}").Order().ToArray();
        Assert.True(intruders.Length == 0,
            "Awaited self-call on a neuron whose target is not [OneWay]/[AlwaysInterleave]: it waits for its own turn. "
            + "Use Neuron.Defer, or list it in SelfCallOwners.json with a reason." + Environment.NewLine + string.Join(Environment.NewLine, intruders));
    }

    [Fact]
    public void EveryOwnerEntryStillMatchesAnUnsafeSite()
    {
        var unsafeSites = Sites().Where(site => !Safe(site)).ToArray();
        var stale = Owners().Where(owner => !unsafeSites.Any(site => owner.File == site.File && owner.Interface == site.Interface && owner.Method == site.Method))
            .Select(owner => $"{owner.File}: {owner.Interface}.{owner.Method}").ToArray();
        Assert.True(stale.Length == 0, "Stale SelfCallOwners.json entries:" + Environment.NewLine + string.Join(Environment.NewLine, stale));
        Assert.All(Owners(), owner => Assert.False(string.IsNullOrWhiteSpace(owner.Reason)));
        Assert.Single(Owners());
    }

    [Fact]
    public void TheScanFindsTheBrainRuntimeApplySite()
        => Assert.Contains(Sites(), site => site.Class == "BrainRuntimeNeuron" && site.Interface == "IBrainProgramChanges" && site.Method == "Apply");
}
