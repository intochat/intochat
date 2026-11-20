using System.Reflection;
using DigitalBrain.UI;
using IntoChat.OS.UI;

namespace IntoChat.OS.Architecture.Tests.Unit;

// The control schema rules (model D): controls with state are control neurons (state, commands, Render); a view node carries no
// Emit, an interactive node is addressable (Key + AutomationName), every non-structural node has a Kit factory, and text controls
// speak Submit, not Changed.
public sealed class ControlSchemaFacts
{
    private static readonly Type[] Nodes = [.. ViewNodeAttribute.DiscoverAll().Values.OrderBy(type => type.Name, StringComparer.Ordinal)];

    // Nodes that are never authored through a Kit factory: nested parts, wire-only or composed by another factory.
    private static readonly HashSet<string> StructuralNodes = new(StringComparer.Ordinal)
    {
        "Intent",
        "Collection",
        "Toast",
        "Badge",
        "Progress",
        "Column", // built by Kit.VStack (a StackBuilder), a name the kind match cannot see
    };

    [Fact]
    public void No_view_node_carries_an_Emit_except_LegacyEmitKinds()
        => Assert.Empty(Nodes
            .Where(type => ConstructorParameters(type).Any(parameter => IsEmit(parameter.ParameterType)))
            .Select(type => type.Name));

    // Holds vacuously until the first control neuron lands under src/IntoChat.OS.Apps/Controls; DartCommandsFacts' samples prove the check.
    [Fact]
    public void Every_control_neuron_has_state_commands_and_Render()
    {
        var problems = NodeSchema.ControlNeurons().SelectMany(ControlNeuronProblems).ToArray();
        Assert.True(problems.Length == 0, string.Join("; ", problems));
    }

    [Fact]
    public void The_control_neuron_rule_bites_on_a_neuron_without_commands_or_state()
    {
        Assert.Empty(ControlNeuronProblems(typeof(DartCommandsFacts.SampleCheckBox)));
        Assert.Contains(ControlNeuronProblems(typeof(DartCommandsFacts.SampleWithoutCommands)), problem => problem.Contains("no command", StringComparison.Ordinal));
        Assert.Contains(ControlNeuronProblems(typeof(DartCommandsFacts.StatelessSample)), problem => problem.Contains("no state", StringComparison.Ordinal));
    }

    [Fact]
    public void Every_interactive_node_has_Key_and_AutomationName()
        => Assert.Empty(Nodes
            .Where(type => ConstructorParameters(type).Any(parameter => IsEmit(parameter.ParameterType) || IsCommand(parameter.ParameterType)))
            .SelectMany(type => new[] { ("Key", typeof(string)), ("AutomationName", typeof(string)) }
                .Where(required => !ConstructorParameters(type).Any(parameter => parameter.Name == required.Item1 && parameter.ParameterType == required.Item2))
                .Select(required => $"{type.Name}.{required.Item1}")));

    [Fact]
    public void Every_view_node_has_a_Kit_factory()
    {
        var factories = typeof(Kit).GetMethods(BindingFlags.Public | BindingFlags.Static).ToArray();
        Assert.Empty(ViewNodeAttribute.DiscoverAll()
            .Where(entry => !StructuralNodes.Contains(entry.Value.Name) && !IsDrawnByANeuron(entry.Value) && !factories.Any(method => Builds(method, entry.Key, entry.Value)))
            .Select(entry => entry.Value.Name));
    }

    [Fact]
    public void No_text_control_has_OnChanged()
        => Assert.Empty(Nodes
            .Where(type => ConstructorParameters(type).Any(parameter => parameter.Name == "Placeholder"))
            .SelectMany(type => ConstructorParameters(type).Where(parameter => parameter.Name == "OnChanged").Select(parameter => $"{type.Name}.{parameter.Name}")));

    [Fact]
    public void The_structural_allow_list_names_real_nodes_without_a_factory()
    {
        var names = Nodes.Select(type => type.Name).ToHashSet(StringComparer.Ordinal);
        var factories = typeof(Kit).GetMethods(BindingFlags.Public | BindingFlags.Static).ToArray();
        var stale = StructuralNodes.Where(name => !names.Contains(name)
            || ViewNodeAttribute.DiscoverAll().Any(entry => entry.Value.Name == name && factories.Any(method => Builds(method, entry.Key, entry.Value)))).ToArray();
        Assert.True(stale.Length == 0, "Remove from the structural allow-list (unknown node or it now has a Kit factory): " + string.Join(", ", stale));
    }

    // A node in DigitalBrain.UI.Controls is what a control neuron draws: apps slot the neuron, they never build its node.
    private static bool IsDrawnByANeuron(Type node) => node.Namespace == typeof(DigitalBrain.UI.Controls.CheckBoxView).Namespace;

    private static bool IsEmit(Type type) => (Nullable.GetUnderlyingType(type) ?? type) == typeof(Emit);

    private static bool IsCommand(Type type) => type == typeof(Command);

    // A control neuron keeps state (a state type with properties), names its props record, takes at least one UI.Commands command through [On], and renders it.
    internal static IEnumerable<string> ControlNeuronProblems(Type neuron)
    {
        var state = NodeSchema.StateOf(neuron);
        if (state is null || NodeSchema.Properties(state).Length == 0) { yield return $"{neuron.Name} keeps no state"; }
        if (NodeSchema.PropsOf(neuron) is null && neuron.Namespace?.StartsWith(NodeSchema.ControlsNamespace, StringComparison.Ordinal) == true)
        {
            yield return $"{neuron.Name} names no props record (derive from ControlNeuron<TState, TProps>)";
        }
        if (NodeSchema.Commands(neuron).Length == 0) { yield return $"{neuron.Name} declares no command ([On] for a {NodeSchema.CommandsNamespace} record)"; }
        if (state is not null && neuron.GetMethod("Render", BindingFlags.NonPublic | BindingFlags.Instance, [state])?.DeclaringType != neuron)
        {
            yield return $"{neuron.Name} does not override Render";
        }
    }

    private static ParameterInfo[] ConstructorParameters(Type type)
        => [.. type.GetConstructors().OrderByDescending(constructor => constructor.GetParameters().Length).First().GetParameters()];

    private static bool Builds(MethodInfo method, string kind, Type node)
    {
        var pascal = char.ToUpperInvariant(kind[0]) + kind[1..];
        var name = method.Name;
        if (name == pascal || name == pascal + "Node" || name == pascal + "Of") { return true; }
        var returned = method.ReturnType;
        return returned == node || returned.Name == node.Name + "Builder" || returned.Name == pascal + "Builder";
    }
}
