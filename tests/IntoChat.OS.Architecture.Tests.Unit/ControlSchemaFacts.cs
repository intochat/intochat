using System.Reflection;
using DigitalBrain.UI;
using IntoChat.OS.UI;

namespace IntoChat.OS.Architecture.Tests.Unit;

// The control schema rules every view node follows: an event is an `On*` parameter that names its payload record, an interactive
// control is addressable (Key + AutomationName), every node has a Kit factory, and text controls speak Submit, not Changed.
// Today's offenders are listed per rule; each fact fails on a new offender and on a listed one that is now fixed, so the lists
// only shrink. Whoever fixes a node removes its entry.
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

    private static readonly HashSet<string> EmitParametersOffenders = new(StringComparer.Ordinal) { "Button.Emit", "Field.OnSubmit", "NavItemNode.Emit", "Switch.Emit" };
    private static readonly HashSet<string> InteractiveControlOffenders = new(StringComparer.Ordinal) { "Button.AutomationName", "Field.AutomationName", "NavItemNode.AutomationName", "Switch.AutomationName" };
    private static readonly HashSet<string> KitFactoryOffenders = new(StringComparer.Ordinal)
    {
        "ApprovalCardNode", "ChoiceCardNode", "Conversation", "DiffCardNode", "DraftCardNode", "ErrorLineNode",
        "JobCardNode", "MetricTileNode", "RecordCardNode", "SecretCardNode", "Switch",
    };
    private static readonly HashSet<string> TextControlOffenders = new(StringComparer.Ordinal) { };

    [Fact]
    public void Every_emit_parameter_is_named_On_and_declares_EventArgs()
        => Check(EmitParametersOffenders, Nodes.SelectMany(type => ConstructorParameters(type)
            .Where(parameter => IsEmit(parameter.ParameterType)
                && (!parameter.Name!.StartsWith("On", StringComparison.Ordinal) || !HasEventArgs(type, parameter)))
            .Select(parameter => $"{type.Name}.{parameter.Name}")));

    [Fact]
    public void Every_interactive_control_has_Key_and_AutomationName()
        => Check(InteractiveControlOffenders, Nodes.Where(type => ConstructorParameters(type).Any(parameter => IsEmit(parameter.ParameterType)))
            .SelectMany(type => new[] { ("Key", typeof(string)), ("AutomationName", typeof(string)) }
                .Where(required => !ConstructorParameters(type).Any(parameter => parameter.Name == required.Item1 && parameter.ParameterType == required.Item2))
                .Select(required => $"{type.Name}.{required.Item1}")));

    [Fact]
    public void Every_view_node_has_a_Kit_factory()
    {
        var factories = typeof(Kit).GetMethods(BindingFlags.Public | BindingFlags.Static).ToArray();
        Check(KitFactoryOffenders, ViewNodeAttribute.DiscoverAll()
            .Where(entry => !StructuralNodes.Contains(entry.Value.Name) && !factories.Any(method => Builds(method, entry.Key, entry.Value)))
            .Select(entry => entry.Value.Name));
    }

    [Fact]
    public void No_text_control_has_OnChanged()
        => Check(TextControlOffenders, Nodes.Where(type => ConstructorParameters(type).Any(parameter => parameter.Name == "Placeholder"))
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

    private static void Check(HashSet<string> known, IEnumerable<string> found)
    {
        var offenders = found.ToHashSet(StringComparer.Ordinal);
        var added = offenders.Except(known).Order(StringComparer.Ordinal).ToArray();
        var fixedOnes = known.Except(offenders).Order(StringComparer.Ordinal).ToArray();
        Assert.True(added.Length == 0, "New offenders (fix them, do not list them): " + string.Join(", ", added));
        Assert.True(fixedOnes.Length == 0, "Fixed, so remove from the known offenders: " + string.Join(", ", fixedOnes));
    }

    private static bool IsEmit(Type type) => (Nullable.GetUnderlyingType(type) ?? type) == typeof(Emit);

    private static ParameterInfo[] ConstructorParameters(Type type)
        => [.. type.GetConstructors().OrderByDescending(constructor => constructor.GetParameters().Length).First().GetParameters()];

    // A positional record parameter's attributes sit on the parameter; [property: ...] puts them on the property.
    private static bool HasEventArgs(Type type, ParameterInfo parameter)
        => parameter.GetCustomAttribute<EventArgsAttribute>() is not null
            || type.GetProperty(parameter.Name!)?.GetCustomAttribute<EventArgsAttribute>() is not null;

    private static bool Builds(MethodInfo method, string kind, Type node)
    {
        var pascal = char.ToUpperInvariant(kind[0]) + kind[1..];
        var name = method.Name;
        if (name == pascal || name == pascal + "Node" || name == pascal + "Of") { return true; }
        var returned = method.ReturnType;
        return returned == node || returned.Name == node.Name + "Builder" || returned.Name == pascal + "Builder";
    }
}
