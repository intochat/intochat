using System.Reflection;
using DigitalBrain.UI;
using IntoChat.OS.UI;

namespace IntoChat.OS.Architecture.Tests.Unit;

// The control schema rules (model D): controls with state are control neurons (state, commands, Render); a view record carries no
// emit at any depth (its action is a Command), an interactive node is addressable (Key + AutomationName), every non-structural node has a Kit factory, text controls
// speak Submit, not Changed, and a control's commands are imperative while what it publishes is past tense.
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

    // Every record a view can hold, nested parts (a conversation's composer, a scene's graph node) included: none has a public Emit
    // member or writes "emit", and the UI model has no Emit type. A stored emit is read as a command when a host loads the view.
    [Fact]
    public void No_view_record_carries_an_Emit_at_any_depth()
    {
        var offenders = Reachable(Nodes)
            .SelectMany(type => type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Where(property => property.Name == "Emit" || (Nullable.GetUnderlyingType(property.PropertyType) ?? property.PropertyType).Name == "Emit"
                    || property.GetCustomAttribute<System.Text.Json.Serialization.JsonPropertyNameAttribute>()?.Name == "emit")
                .Select(property => $"{type.Name}.{property.Name}"))
            .ToArray();

        Assert.True(offenders.Length == 0, string.Join(", ", offenders));
        Assert.Null(typeof(Node).Assembly.GetType("DigitalBrain.UI.Emit"));
        Assert.Contains(typeof(ComposerNode), Reachable(Nodes));
    }

    // The UI model's types reachable from the view nodes through their public properties (element and nullable types unwrapped).
    private static HashSet<Type> Reachable(IEnumerable<Type> roots)
    {
        var seen = new HashSet<Type>();
        var pending = new Stack<Type>(roots);
        while (pending.TryPop(out var type))
        {
            if (type.Assembly != typeof(Node).Assembly || !seen.Add(type)) { continue; }
            foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                var propertyType = Nullable.GetUnderlyingType(property.PropertyType) ?? property.PropertyType;
                pending.Push(propertyType.IsArray ? propertyType.GetElementType()! : propertyType);
                foreach (var argument in propertyType.IsGenericType ? propertyType.GetGenericArguments() : []) { pending.Push(argument); }
            }
        }
        return seen;
    }

    // DartCommandsFacts' samples prove the check bites.
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
    public void A_control_command_is_imperative_and_what_it_publishes_is_past_tense()
    {
        var problems = NodeSchema.ControlNeurons().SelectMany(neuron => NamingProblems(neuron, NodeSchema.Commands(neuron), NodeSchema.Signals(neuron))).ToArray();
        Assert.True(problems.Length == 0, string.Join("; ", problems));
    }

    [Fact]
    public void The_naming_rule_bites_on_a_past_tense_command_an_imperative_fact_and_a_record_that_is_both()
    {
        Assert.Empty(NamingProblems(typeof(DartCommandsFacts.SampleCheckBox), [typeof(DigitalBrain.UI.Commands.SetChecked)], [typeof(DigitalBrain.UI.Commands.Toggled)]));
        Assert.Contains(NamingProblems(typeof(DartCommandsFacts.SampleCheckBox), [typeof(DigitalBrain.UI.Commands.Toggled)], []), problem => problem.Contains("not imperative", StringComparison.Ordinal));
        Assert.Contains(NamingProblems(typeof(DartCommandsFacts.SampleCheckBox), [], [typeof(DigitalBrain.UI.Commands.Expand)]), problem => problem.Contains("not past tense", StringComparison.Ordinal));
        Assert.Contains(NamingProblems(typeof(DartCommandsFacts.SampleCheckBox), [typeof(DigitalBrain.UI.Commands.Expanded)], [typeof(DigitalBrain.UI.Commands.Expanded)]),
            problem => problem.Contains("both", StringComparison.Ordinal));
    }

    // A command ([On]) starts with a verb in the imperative and does not end in a past participle, except a "Set" of one (SetChecked);
    // a fact ([Publishes]) ends in a past participle (Toggled, SelectionChanged); no record is both.
    internal static IEnumerable<string> NamingProblems(Type neuron, IEnumerable<Type> commands, IEnumerable<Type> facts)
    {
        var commandSet = commands.ToHashSet();
        foreach (var command in commandSet)
        {
            var words = Words(command.Name);
            if (IsPast(words[0]) || (IsPast(words[^1]) && words[0] != "Set")) { yield return $"{neuron.Name} command {command.Name} is not imperative"; }
        }
        foreach (var fact in facts)
        {
            if (!IsPast(Words(fact.Name)[^1])) { yield return $"{neuron.Name} publishes {fact.Name}, which is not past tense"; }
            if (commandSet.Contains(fact)) { yield return $"{neuron.Name} both takes and publishes {fact.Name}"; }
        }
    }

    private static string[] Words(string name) => System.Text.RegularExpressions.Regex.Split(name, "(?<!^)(?=[A-Z])");

    private static bool IsPast(string word) => word.EndsWith("ed", StringComparison.Ordinal);

    [Fact]
    public void Every_interactive_node_has_Key_and_AutomationName()
        => Assert.Empty(Nodes
            .Where(type => ConstructorParameters(type).Any(parameter => IsCommand(parameter.ParameterType)))
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
