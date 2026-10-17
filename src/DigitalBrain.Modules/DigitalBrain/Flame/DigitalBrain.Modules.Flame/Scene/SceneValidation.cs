using System.Globalization;
using System.Text;
using System.Text.Json;
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
            CheckAgainstSchema(node);
            if (node.Tree is not null) { CheckTree(node.Tree, KeysOf(node)); }
            foreach (var child in node.Children) { Walk(child); }
        }
        foreach (var node in definition.Nodes) { Walk(node); }
    }

    public static void CheckTree(BehaviorTree tree, string[] declaredKeys) => FlameSchema.CheckTree(tree, declaredKeys);

    // The schema is the grammar: a known node type is checked against its parameter spec, so a scene the renderer
    // cannot draw is refused at Set. An unknown node type is left alone — an older shell renders it as a placeholder.
    private static void CheckAgainstSchema(SceneNode node)
    {
        var spec = FlameSchema.For(node.Type);
        if (spec is null) { return; }
        foreach (var (key, value) in node.Parameters)
        {
            var param = spec.Parameters.FirstOrDefault(p => string.Equals(p.Key, key, StringComparison.Ordinal))
                ?? throw new ArgumentException($"Node '{node.Id}' of type '{node.Type}' has no parameter '{key}'.");
            if (!Parses(param, value)) { throw new ArgumentException($"Node '{node.Id}' parameter '{key}' is not a valid {param.Kind}."); }
        }
        foreach (var param in spec.Parameters.Where(p => p.Required))
        {
            if (!node.Parameters.ContainsKey(param.Key)) { throw new ArgumentException($"Node '{node.Id}' of type '{node.Type}' needs parameter '{param.Key}'."); }
        }
        foreach (var input in node.Inputs)
        {
            if (!FlameSchema.Gestures.Contains(input, StringComparer.Ordinal)) { throw new ArgumentException($"Node '{node.Id}' reports unknown gesture '{input}'."); }
        }
        foreach (var tween in node.Tweens)
        {
            if (!FlameSchema.TweenProperties.Contains(tween.Property, StringComparer.Ordinal)) { throw new ArgumentException($"Node '{node.Id}' tweens unknown property '{tween.Property}'."); }
            if (!FlameSchema.TweenCurves.Contains(tween.Curve, StringComparer.Ordinal)) { throw new ArgumentException($"Node '{node.Id}' tweens with unknown curve '{tween.Curve}'."); }
        }
    }

    // The keys a sphere's tree may test besides the reserved ones: the property names of its blackboard object.
    private static string[] KeysOf(SceneNode node)
    {
        if (!node.Parameters.TryGetValue("blackboard", out var json)) { return []; }
        using var document = JsonDocument.Parse(json);
        return document.RootElement.ValueKind == JsonValueKind.Object ? [.. document.RootElement.EnumerateObject().Select(property => property.Name)] : [];
    }

    private static bool Parses(FlameParam param, string value) => param.Kind switch
    {
        FlameParamKind.Text => value.Length > 0,
        FlameParamKind.Number => double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out _),
        FlameParamKind.Color => value.Length is 7 or 9 && value[0] == '#' && value[1..].All(Uri.IsHexDigit),
        FlameParamKind.Json => IsJson(value),
        FlameParamKind.Choice => param.Choices!.Contains(value, StringComparer.Ordinal),
        FlameParamKind.Boolean => bool.TryParse(value, out _),
        _ => false,
    };

    private static bool IsJson(string value)
    {
        try { using var _ = JsonDocument.Parse(value); return true; }
        catch (JsonException) { return false; }
    }
}
