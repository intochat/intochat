using System.Text;
using DigitalBrain.Flame.Scene;

namespace DigitalBrain.Flame;

internal static class SceneValidation
{
    public static void Check(SceneDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var count = 0;
        var bytes = 0L;
        void Walk(SceneNode node)
        {
            if (string.IsNullOrWhiteSpace(node.Id)) { throw new ArgumentException("Every node needs an id."); }
            if (!ids.Add(node.Id)) { throw new ArgumentException($"Node id '{node.Id}' appears twice."); }
            if (++count > FlameVocabulary.MaxNodes) { throw new ArgumentException($"A scene holds at most {FlameVocabulary.MaxNodes} nodes."); }
            foreach (var (k, v) in node.Parameters) { bytes += Encoding.UTF8.GetByteCount(k) + Encoding.UTF8.GetByteCount(v); }
            if (bytes > FlameVocabulary.MaxParameterBytes) { throw new ArgumentException($"Scene parameters exceed {FlameVocabulary.MaxParameterBytes} bytes."); }
            foreach (var child in node.Children) { Walk(child); }
        }
        foreach (var node in definition.Nodes) { Walk(node); }
        var machines = new HashSet<string>(StringComparer.Ordinal);
        foreach (var machine in definition.Machines)
        {
            if (!machines.Add(machine.Id)) { throw new ArgumentException($"Machine id '{machine.Id}' appears twice."); }
            var states = machine.States.Select(s => s.Name).ToHashSet(StringComparer.Ordinal);
            if (!states.Contains(machine.Initial)) { throw new ArgumentException($"Machine '{machine.Id}' starts in unknown state '{machine.Initial}'."); }
            foreach (var t in machine.Transitions)
            {
                if (!states.Contains(t.From)) { throw new ArgumentException($"Machine '{machine.Id}' transitions from unknown state '{t.From}'."); }
                if (!states.Contains(t.To)) { throw new ArgumentException($"Machine '{machine.Id}' transitions to unknown state '{t.To}'."); }
                if (string.IsNullOrWhiteSpace(t.Event)) { throw new ArgumentException($"Machine '{machine.Id}' has a transition without an event."); }
            }
            foreach (var patch in machine.States.SelectMany(s => s.Apply))
            {
                if (!ids.Contains(patch.NodeId)) { throw new ArgumentException($"Machine '{machine.Id}' patches unknown node '{patch.NodeId}'."); }
            }
        }
    }
}
