using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using Orleans;

namespace IntoChat.OS.Architecture.Tests.Unit;

// Phase 6 guard rails (D-03, D-04, D-06, D-16): neurons leave Orleans family by family, and every family PR shrinks
// these baselines. They are captured, never hand-edited, and each fact is two-sided: growth fails, and a drop fails
// until the baseline is shrunk so the next regression cannot hide in the slack.
// The kernel composition (the IntoChat.OS.Kernel.Composition library, R4-12; its Kernel/ folder by file) is where Orleans
// may stay: kernel typed grains, the transitional legacy sink and the Authority accessor live there and are exempt. The
// legacy cluster neuron in the same library (namespace IntoChat.OS.Modules.Microsoft.Orleans) is counted until 06-03.
public sealed partial class NeuronMigrationFacts
{
    internal const string KernelCompositionPrefix = "IntoChat.OS.Kernel.Composition";

    // Project-key form of the same place: src/IntoChat.OS.Kernel/IntoChat.OS.Kernel.Composition/Kernel.
    internal const string KernelCompositionFolder = "IntoChat.OS.Kernel.Composition/Kernel/";

    internal const string StillGrainsFile = "NeuronsStillGrains.json";
    internal const string AttributeFile = "OrleansAttributeOwners.json";
    internal const string InterfaceFile = "GrainInterfaceOwners.json";
    internal const string GrainTypeFile = "NeuronGrainTypes.json";

    // Store and StateName list every [PersistentState] parameter in declaration order, joined with ", " (a class with
    // two states pins both); null when the class persists nothing.
    internal sealed record GrainTypePin(string Type, string? Store, string? StateName);

    internal static bool InKernelComposition(string fullName)
        => fullName.StartsWith(KernelCompositionPrefix + ".", StringComparison.Ordinal);

    internal static bool InKernelCompositionFile(string projectKey)
        => projectKey.StartsWith(KernelCompositionFolder, StringComparison.Ordinal);

    internal static string[] CurrentStillGrains()
        => CaptureWriter.Ordered(SourceTypes.Grains.Value.Where(type => !InKernelComposition(type.FullName)).Select(type => type.FullName));

    // Every activatable neuron: concrete grain classes outside the kernel composition, and plain classes that carry
    // DigitalBrain [Neuron(type)] (os declares no Neuron attribute of its own, so the simple name is the core one).
    internal static Dictionary<string, GrainTypePin> CurrentGrainTypes()
    {
        var grains = SourceTypes.Grains.Value.Where(type => !type.IsAbstract && !InKernelComposition(type.FullName));
        var plain = SourceTypes.All.Value.Where(type => !type.IsInterface && type.Attributes.Any(attribute => SourceTypes.AttributeName(attribute) == "Neuron"));
        return grains.Concat(plain).DistinctBy(type => type.FullName)
            .ToDictionary(type => type.FullName, Pin, StringComparer.Ordinal);
    }

    private static GrainTypePin Pin(SourceTypes.Declared type)
    {
        var named = type.Attributes.FirstOrDefault(attribute => SourceTypes.AttributeName(attribute) is "GrainType" or "Neuron");
        var argument = named?.ArgumentList?.Arguments.FirstOrDefault()?.Expression;
        var grainType = argument is null ? DefaultGrainType(type.Name) : SourceTypes.Resolve(argument, type)
            ?? throw new InvalidOperationException($"{type.FullName}: grain type argument `{argument}` does not resolve to a string constant.");
        var states = type.StateParameters.Select(parameter =>
        {
            var attribute = parameter.AttributeLists.SelectMany(list => list.Attributes).First(SourceTypes.IsPersistentState);
            var arguments = attribute.ArgumentList?.Arguments ?? default;
            string? Arg(int index) => arguments.Count > index ? SourceTypes.Resolve(arguments[index].Expression, type) ?? "?" + arguments[index].Expression : null;
            return (Name: Arg(0) ?? "state", Store: Arg(1) ?? "Default");
        }).ToArray();
        if (states.Length == 0 && PlainStateful(type))
        {
            // A plain stateful neuron (06-04) persists in the runtime's cluster store under [Neuron(State = ...)] or "state",
            // the store and name its [PersistentState] grain pinned.
            var stateName = named?.ArgumentList?.Arguments.FirstOrDefault(argument => argument.NameEquals?.Name.Identifier.Text == "State")?.Expression;
            return new GrainTypePin(grainType, PlainStore, stateName is null ? "state" : SourceTypes.Resolve(stateName, type) ?? "?" + stateName);
        }
        return states.Length == 0
            ? new GrainTypePin(grainType, null, null)
            : new GrainTypePin(grainType, string.Join(", ", states.Select(state => state.Store)), string.Join(", ", states.Select(state => state.Name)));
    }

    // The store plain neurons' state lives in: the runtime's cluster store (R4-09), named as the grains named theirs.
    private const string PlainStore = "cluster";

    // A class deriving DigitalBrain.Neuron<TState>, directly or through a scanned base (Flutter's UiNeuron<TState>).
    private static bool PlainStateful(SourceTypes.Declared type)
    {
        var classes = SourceTypes.All.Value.Where(candidate => !candidate.IsInterface)
            .GroupBy(candidate => candidate.Name.Split('`')[0]).ToDictionary(group => group.Key, group => group.First());
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var current = type; current is not null && seen.Add(current.FullName);)
        {
            if (current.PlainStateBase) { return true; }
            var next = current.Bases.FirstOrDefault(classes.ContainsKey);
            current = next is null ? null : classes[next];
        }
        return false;
    }

    // Orleans' convention when a grain class names no [GrainType]: the class name lower-cased, arity dropped, a "grain"
    // suffix dropped (GrainTypeResolver), and generics append the arity. PersistentState() without a name is "state";
    // without a store it is Orleans' "Default" provider.
    internal static string DefaultGrainType(string className)
    {
        var tick = className.IndexOf('`', StringComparison.Ordinal);
        var name = (tick > 0 ? className[..tick] : className).ToLowerInvariant();
        if (name.EndsWith("grain", StringComparison.Ordinal) && name.Length > "grain".Length) { name = name[..^"grain".Length]; }
        return tick > 0 ? name + className[tick..] : name;
    }

    [GeneratedRegex(@"\[(?:GrainType|Reentrant|AlwaysInterleave|MayInterleave|ResponseTimeout|GenerateSerializer|PersistentState)\b|\[ReadOnly\]|\[OneWay\]|\[Alias\(|^\s*(?:global\s+)?using\s+(?:static\s+)?Orleans\b",
        RegexOptions.CultureInvariant | RegexOptions.Multiline)]
    private static partial Regex OrleansAttribute();

    internal static Dictionary<string, int> CurrentOrleansAttributes()
        => Repository.ProjectFiles("*.cs", path => !path.EndsWith(".g.cs", StringComparison.OrdinalIgnoreCase))
            .Select(path => (Key: Repository.ProjectKey(path), Count: OrleansAttribute().Count(File.ReadAllText(path))))
            .Where(file => file.Count > 0 && !InKernelCompositionFile(file.Key))
            .ToDictionary(file => file.Key, file => file.Count, StringComparer.Ordinal);

    internal static Dictionary<string, int> CurrentGrainInterfaces()
        => SourceTypes.GrainInterfaces.Value.Where(type => !InKernelComposition(type.FullName))
            .GroupBy(type => type.File, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);

    // D-03: the set of classes still deriving Grain only shrinks.
    [Fact]
    public void NeuronsStillGrainsOnlyShrinks()
    {
        var baseline = Baseline<string[]>(StillGrainsFile).ToHashSet(StringComparer.Ordinal);
        var current = CurrentStillGrains();

        var added = current.Where(name => !baseline.Contains(name)).ToArray();
        Assert.True(added.Length == 0,
            "New classes deriving Orleans Grain; write a plain DigitalBrain [Neuron] class instead:\n" + string.Join('\n', added));

        var migrated = baseline.Where(name => !current.Contains(name, StringComparer.Ordinal)).Order(StringComparer.Ordinal).ToArray();
        Assert.True(migrated.Length == 0, $"These classes no longer derive Grain; shrink the file {StillGrainsFile}:\n" + string.Join('\n', migrated));
    }

    // D-06, D-16: Orleans attributes and usings per hand-written file only shrink (the runtime owns them).
    [Fact]
    public void OrleansAttributesOnlyShrink() => CountsOnlyShrink(AttributeFile, CurrentOrleansAttributes(),
        "New Orleans attributes or usings; use the neutral DigitalBrain attributes (D-06)");

    // D-16: grain interfaces per file only shrink; typed contracts become plain interfaces reached through Get<T>.
    [Fact]
    public void GrainInterfacesOnlyShrink() => CountsOnlyShrink(InterfaceFile, CurrentGrainInterfaces(),
        "New grain interfaces; declare a plain contract interface (D-11, D-16)");

    // Storage keys derive from the grain type: a migrated class carries [Neuron(type)] with exactly the pinned string,
    // and its state keeps the pinned store and name.
    [Fact]
    public void GrainTypesNeverMove()
    {
        var baseline = Baseline<Dictionary<string, GrainTypePin>>(GrainTypeFile);
        var current = CurrentGrainTypes();

        var moved = current.Where(entry => baseline.TryGetValue(entry.Key, out var pinned) && pinned != entry.Value)
            .Select(entry => $"{entry.Key}: {baseline[entry.Key]} -> {entry.Value}").Order(StringComparer.Ordinal).ToArray();
        Assert.True(moved.Length == 0, "Grain type, store or state name moved; persisted state would be orphaned:\n" + string.Join('\n', moved));

        var added = current.Keys.Where(name => !baseline.ContainsKey(name)).Order(StringComparer.Ordinal).ToArray();
        Assert.True(added.Length == 0, $"New neuron classes; capture {GrainTypeFile} (CaptureNeuronGrainTypes):\n" + string.Join('\n', added));

        var gone = baseline.Keys.Where(name => !current.ContainsKey(name)).Order(StringComparer.Ordinal).ToArray();
        Assert.True(gone.Length == 0,
            $"Pinned neurons are gone (renamed, deleted or lost their [Neuron] type); review and recapture {GrainTypeFile}:\n" + string.Join('\n', gone));
    }

    // The source scan is the ratchet's eye (it must see test projects); on production code it must see exactly what
    // reflection over the loaded assemblies sees: the same grain classes, the same grain interfaces, the same pins.
    [Fact]
    public void SourceTypesAgreeWithReflection()
    {
        var production = ProductionArchitecture.Assemblies.Select(assembly => assembly.GetName().Name!).ToHashSet(StringComparer.Ordinal);
        var types = ProductionArchitecture.Types.Where(type => type.Assembly.GetName().Name!.StartsWith("IntoChat.", StringComparison.Ordinal)).ToArray();

        var reflectedGrains = types.Where(type => type.IsClass && typeof(Grain).IsAssignableFrom(type)).Select(type => type.FullName!).ToHashSet(StringComparer.Ordinal);
        var scannedGrains = SourceTypes.Grains.Value.Where(type => production.Contains(type.Project)).Select(type => type.FullName).ToHashSet(StringComparer.Ordinal);
        Assert.Equal(reflectedGrains.Order(StringComparer.Ordinal), scannedGrains.Order(StringComparer.Ordinal));

        var reflectedInterfaces = types.Where(type => type.IsInterface && typeof(IGrain).IsAssignableFrom(type)).Select(type => type.FullName!).ToHashSet(StringComparer.Ordinal);
        var scannedInterfaces = SourceTypes.GrainInterfaces.Value.Where(type => production.Contains(type.Project)).Select(type => type.FullName).ToHashSet(StringComparer.Ordinal);
        Assert.Equal(reflectedInterfaces.Order(StringComparer.Ordinal), scannedInterfaces.Order(StringComparer.Ordinal));

        var scannedPins = CurrentGrainTypes();
        var mismatched = types.Where(type => scannedPins.ContainsKey(type.FullName!))
            .Select(type => (Name: type.FullName!, Reflected: ReflectedPin(type), Scanned: scannedPins[type.FullName!]))
            .Where(pin => pin.Reflected != pin.Scanned)
            .Select(pin => $"{pin.Name}: reflection {pin.Reflected}, source {pin.Scanned}").ToArray();
        Assert.True(mismatched.Length == 0, "Source scan and reflection disagree on grain types:\n" + string.Join('\n', mismatched));
    }

    private static GrainTypePin ReflectedPin(Type type)
    {
        var named = type.GetCustomAttributesData().FirstOrDefault(attribute =>
            attribute.AttributeType.FullName is "Orleans.GrainTypeAttribute" or "DigitalBrain.NeuronAttribute");
        var grainType = named?.ConstructorArguments.FirstOrDefault().Value as string ?? DefaultGrainType(type.Name);
        var states = type.GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
            .SelectMany(constructor => constructor.GetParameters())
            .Select(parameter => parameter.GetCustomAttributesData().FirstOrDefault(attribute => attribute.AttributeType.Name == "PersistentStateAttribute"))
            .OfType<CustomAttributeData>()
            .Select(attribute => (Name: attribute.ConstructorArguments.ElementAtOrDefault(0).Value as string ?? "state",
                Store: attribute.ConstructorArguments.ElementAtOrDefault(1).Value as string ?? "Default"))
            .ToArray();
        if (states.Length == 0 && Stateful(type))
        {
            var stateName = named?.NamedArguments.FirstOrDefault(argument => argument.MemberName == "State").TypedValue.Value as string;
            return new GrainTypePin(grainType, PlainStore, stateName ?? "state");
        }
        return states.Length == 0
            ? new GrainTypePin(grainType, null, null)
            : new GrainTypePin(grainType, string.Join(", ", states.Select(state => state.Store)), string.Join(", ", states.Select(state => state.Name)));
    }

    private static bool Stateful(Type type)
    {
        for (var current = type.BaseType; current is not null; current = current.BaseType)
        {
            if (current.IsGenericType && current.GetGenericTypeDefinition().FullName == "DigitalBrain.Neuron`1") { return true; }
        }
        return false;
    }

    internal static void CountsOnlyShrink(string file, Dictionary<string, int> current, string growth)
    {
        var baseline = Baseline<Dictionary<string, int>>(file);
        var grown = current.Where(entry => entry.Value > baseline.GetValueOrDefault(entry.Key))
            .Select(entry => $"{entry.Key}: {baseline.GetValueOrDefault(entry.Key)} -> {entry.Value}").Order(StringComparer.Ordinal).ToArray();
        Assert.True(grown.Length == 0, $"{growth}, or deliberately raise {file}:\n" + string.Join('\n', grown));

        var shrunk = baseline.Where(entry => current.GetValueOrDefault(entry.Key) < entry.Value)
            .Select(entry => $"{entry.Key}: {entry.Value} -> {current.GetValueOrDefault(entry.Key)}").Order(StringComparer.Ordinal).ToArray();
        Assert.True(shrunk.Length == 0, $"Counts went down; shrink the file {file} so the ratchet holds:\n" + string.Join('\n', shrunk));
    }

    internal static T Baseline<T>(string file) => JsonSerializer.Deserialize<T>(File.ReadAllText(CaptureWriter.Path(file)))!;

    [Fact(Explicit = true)]
    public void CaptureNeuronsStillGrains() => CaptureWriter.Write(StillGrainsFile, CurrentStillGrains());

    [Fact(Explicit = true)]
    public void CaptureOrleansAttributeOwners() => CaptureWriter.Write(AttributeFile, CaptureWriter.Sorted(AttributeFile, CurrentOrleansAttributes()));

    [Fact(Explicit = true)]
    public void CaptureGrainInterfaceOwners() => CaptureWriter.Write(InterfaceFile, CaptureWriter.Sorted(InterfaceFile, CurrentGrainInterfaces()));

    [Fact(Explicit = true)]
    public void CaptureNeuronGrainTypes()
        => CaptureWriter.Write(GrainTypeFile, CaptureWriter.Sorted(GrainTypeFile, CurrentGrainTypes().OrderBy(entry => entry.Key, StringComparer.Ordinal)));
}
