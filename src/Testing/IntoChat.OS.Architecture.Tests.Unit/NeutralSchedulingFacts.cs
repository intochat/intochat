using System.Reflection;

namespace IntoChat.OS.Architecture.Tests.Unit;

// (06-05) A plain neuron's scheduling is read by the runtime from DigitalBrain's attributes. IntoChat.OS keeps legacy attributes
// of the same names ([Interleaves], [ReadOnlyCall], [OneWayCall], [InterleavesWhen], [CallTimeout]) for the Framework path, and
// inside an IntoChat.OS.* namespace the unqualified name binds to those: the code compiles and the runtime silently runs every
// call as one turn. So no plain contract or [Neuron] class may carry the os ones.
public sealed class NeutralSchedulingFacts
{
    private static readonly HashSet<string> Legacy = new(StringComparer.Ordinal)
    {
        "IntoChat.OS.InterleavesAttribute",
        "IntoChat.OS.ReadOnlyCallAttribute",
        "IntoChat.OS.OneWayCallAttribute",
        "IntoChat.OS.InterleavesWhenAttribute",
        "IntoChat.OS.CallTimeoutAttribute",
    };

    [Fact]
    public void PlainNeuronsAndContractsUseTheRuntimesSchedulingAttributes()
    {
        var plain = ProductionArchitecture.Types.Where(type =>
            (type.IsClass && type.IsDefined(typeof(DigitalBrain.Neurons.NeuronAttribute), inherit: false))
            || (type.IsInterface && type.IsDefined(typeof(DigitalBrain.Contracts.DefaultNeuronTypeAttribute), inherit: false)));
        const BindingFlags Members = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;
        var wrong = plain.SelectMany(type => Hierarchy(type).SelectMany(level =>
                level.GetCustomAttributesData().Select(attribute => (Where: level.FullName!, attribute))
                    .Concat(level.GetMethods(Members).SelectMany(method => method.GetCustomAttributesData().Select(attribute => (Where: $"{level.FullName}.{method.Name}", attribute))))))
            .Where(entry => Legacy.Contains(entry.attribute.AttributeType.FullName!))
            .Select(entry => $"{entry.Where}: [{entry.attribute.AttributeType.Name}] is the os legacy attribute; write DigitalBrain.{entry.attribute.AttributeType.Name.Replace("Attribute", "", StringComparison.Ordinal)}")
            .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        Assert.True(wrong.Length == 0, string.Join('\n', wrong));
    }

    private static IEnumerable<Type> Hierarchy(Type type)
    {
        if (type.IsInterface) { return [type, .. type.GetInterfaces()]; }
        var levels = new List<Type>();
        for (var current = type; current is not null && current != typeof(object); current = current.BaseType) { levels.Add(current); }
        return levels;
    }
}
