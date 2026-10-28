using System.Text.RegularExpressions;

namespace IntoChat.OS.Architecture.Tests.Unit;

// Phase 6 closure (06-09, success criterion 4): grain interfaces and the attributes that shape a grain's scheduling or storage
// live only in the kernel composition and with the allow-listed still-grains (NeuronsStillGrains.json, which only shrinks).
// Strict facts, not ratchets: a new grain interface or grain attribute anywhere else fails at once. The serializer attributes
// (GenerateSerializer, Id, Alias and their Orleans using) are wire and persisted format, not grain shape; they stay on the
// OrleansAttributeOwners ratchet until Phase 12 retires the Orleans codecs with Authority. This project (the checker) quotes
// the attributes in its messages and is not scanned.
public sealed partial class OrleansBanFacts
{
    // Phase 12 vocabulary leftovers, closed: each names why it is not a still-grain's and where it goes. A grain interface
    // here is one only through the member-less INeuron marker (itself IGrainWithStringKey until Phase 12 drops the marker).
    private static readonly Dictionary<string, string> Leftovers = new(StringComparer.Ordinal);

    [GeneratedRegex(@"^(?!\s*//).*?\[(?:[\w.]*\s*,\s*)*(?:GrainType|AlwaysInterleave|MayInterleave|ResponseTimeout|PersistentState|StatelessWorker)\b|^(?!\s*//).*\[(?:ReadOnly|OneWay)\]",
        RegexOptions.CultureInvariant | RegexOptions.Multiline)]
    private static partial Regex GrainAttribute();

    // Classes that may be grains: the allow-list, and the kernel composition's own grains.
    private static SourceTypes.Declared[] PermittedGrains()
    {
        var allowList = NeuronMigrationFacts.Baseline<string[]>(NeuronMigrationFacts.StillGrainsFile).ToHashSet(StringComparer.Ordinal);
        return [.. SourceTypes.Grains.Value.Where(type => allowList.Contains(type.FullName) || NeuronMigrationFacts.InKernelComposition(type.FullName))];
    }

    // The grain interfaces the permitted grains implement, with every grain interface those derive.
    private static HashSet<string> PermittedInterfaces()
    {
        var interfaces = SourceTypes.GrainInterfaces.Value.GroupBy(type => type.Name.Split('`')[0], StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.Ordinal);
        var permitted = new HashSet<string>(StringComparer.Ordinal);
        var pending = new Queue<string>(PermittedGrains().SelectMany(type => type.Bases));
        while (pending.TryDequeue(out var name))
        {
            if (!interfaces.TryGetValue(name.Split('`')[0], out var matches)) { continue; }
            foreach (var match in matches.Where(match => permitted.Add(match.FullName)))
            {
                foreach (var next in match.Bases) { pending.Enqueue(next); }
            }
        }
        return permitted;
    }

    // Leftovers and their derivatives (a test contract whose only grain base is INeuron) are vocabulary, not grain shape.
    private static bool Leftover(SourceTypes.Declared type)
    {
        if (Leftovers.ContainsKey(type.FullName)) { return true; }
        var interfaces = SourceTypes.GrainInterfaces.Value.GroupBy(each => each.Name.Split('`')[0], StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.Ordinal);
        var grainBases = type.Bases.Select(name => name.Split('`')[0])
            .Where(name => SourceTypes.GrainInterfaceRoots.Contains(name) || interfaces.ContainsKey(name)).ToArray();
        return grainBases.Length > 0 && grainBases.All(name => interfaces.TryGetValue(name, out var bases) && bases.All(Leftover));
    }

    [Fact]
    public void GrainInterfacesServeOnlyAllowListedGrains()
    {
        var permitted = PermittedInterfaces();
        var offenders = SourceTypes.GrainInterfaces.Value
            .Where(type => !NeuronMigrationFacts.InKernelComposition(type.FullName) && !permitted.Contains(type.FullName) && !Leftover(type))
            .Select(type => $"{type.FullName} ({type.File})").Order(StringComparer.Ordinal).ToArray();
        Assert.True(offenders.Length == 0,
            "Grain interfaces no allow-listed still-grain implements; declare a plain contract a [Neuron] class implements (D-11, D-16):\n" + string.Join('\n', offenders));
    }

    private const string Checker = "IntoChat.OS.Architecture.Tests.Unit/";

    [Fact]
    public void GrainAttributesStayOnAllowListedGrains()
    {
        var permitted = PermittedInterfaces();
        var files = PermittedGrains().Select(type => type.File)
            .Concat(SourceTypes.GrainInterfaces.Value.Where(type => permitted.Contains(type.FullName) || Leftovers.ContainsKey(type.FullName)).Select(type => type.File))
            .ToHashSet(StringComparer.Ordinal);
        var offenders = Repository.ProjectFiles("*.cs", path => !path.EndsWith(".g.cs", StringComparison.OrdinalIgnoreCase))
            .Select(path => (Key: Repository.ProjectKey(path), Text: File.ReadAllText(path)))
            .Where(file => !NeuronMigrationFacts.InKernelCompositionFile(file.Key) && !file.Key.StartsWith(Checker, StringComparison.Ordinal)
                && !files.Contains(file.Key) && GrainAttribute().IsMatch(file.Text))
            .Select(file => file.Key).Order(StringComparer.Ordinal).ToArray();
        Assert.True(offenders.Length == 0,
            "Orleans grain attributes outside the kernel composition and the allow-listed still-grains; use the neutral DigitalBrain attributes (D-06):\n"
            + string.Join('\n', offenders));
    }

    [Fact]
    public void LeftoversAreStillGrainInterfaces()
    {
        var known = SourceTypes.GrainInterfaces.Value.Select(type => type.FullName).ToHashSet(StringComparer.Ordinal);
        var stale = Leftovers.Keys.Where(name => !known.Contains(name)).Order(StringComparer.Ordinal).ToArray();
        Assert.True(stale.Length == 0, "Stale Orleans ban leftovers; drop them from OrleansBanFacts.Leftovers:\n" + string.Join('\n', stale));
    }
}
