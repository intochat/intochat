using System.Globalization;
using System.Text.Json;
using DigitalBrain.Neurons;
using IntoChat.OS.Modules.UI.PropertyGrid.Signals;

namespace IntoChat.OS.Modules.UI.PropertyGrid;

[Neuron("ui.property-grid")]
internal sealed class PropertyGridNeuron : Neuron<PropertyGridState>, IPropertyGrid
{
    private const int MaxEntries = 500;

    public async Task<PropertyGridState> Configure(string title, IReadOnlyList<PropertyGridEntry> entries, bool readOnly = true)
    {
        ArgumentNullException.ThrowIfNull(entries);
        var arr = entries.Take(MaxEntries).ToArray();
        var expanded = arr.Where(e => e.HasChildren && e.Depth <= 1).Select(e => e.Path).ToArray();
        var next = State with
        {
            Version = State.Version + 1,
            Title = string.IsNullOrWhiteSpace(title) ? "Properties" : title.Trim(),
            Entries = arr,
            ExpandedPaths = expanded,
            SelectedPath = arr.FirstOrDefault()?.Path,
            ReadOnly = readOnly,
        };
        await Save(next, new PropertyGridChanged(Id.FullKey(), next.Version, next.SelectedPath));
        return next;
    }

    public async Task<PropertyGridState> LoadJson(string title, string json, bool readOnly = true)
    {
        var raw = string.IsNullOrWhiteSpace(json) ? "{}" : json;
        var entries = ParseJsonEntries(raw);
        var expanded = entries.Where(e => e.HasChildren && e.Depth <= 1).Select(e => e.Path).ToArray();
        var selected = State.SelectedPath is not null && entries.Any(e => string.Equals(e.Path, State.SelectedPath, StringComparison.Ordinal))
            ? State.SelectedPath
            : entries.FirstOrDefault()?.Path;

        var next = State with
        {
            Version = State.Version + 1,
            Title = string.IsNullOrWhiteSpace(title) ? "Properties" : title.Trim(),
            Entries = entries,
            ExpandedPaths = expanded,
            SelectedPath = selected,
            ReadOnly = readOnly,
            RawJson = raw,
        };
        await Save(next, new PropertyGridChanged(Id.FullKey(), next.Version, next.SelectedPath));
        return next;
    }

    public async Task<PropertyGridState> ToggleExpand(string path)
    {
        var set = State.ExpandedPaths.ToHashSet(StringComparer.Ordinal);
        if (!set.Remove(path))
        {
            set.Add(path);
        }
        var next = State with { Version = State.Version + 1, ExpandedPaths = [.. set] };
        await Save(next, new PropertyGridChanged(Id.FullKey(), next.Version, next.SelectedPath));
        return next;
    }

    public async Task<PropertyGridState> Select(string path)
    {
        if (State.Entries.All(e => !string.Equals(e.Path, path, StringComparison.Ordinal)))
        {
            throw new ArgumentException($"Unknown property path '{path}'.", nameof(path));
        }
        var next = State with { Version = State.Version + 1, SelectedPath = path };
        await Save(next, new PropertyGridChanged(Id.FullKey(), next.Version, next.SelectedPath));
        return next;
    }

    public async Task<PropertyGridState> SetFilter(string filter)
    {
        var next = State with { Version = State.Version + 1, Filter = filter?.Trim() ?? "" };
        await Save(next, new PropertyGridChanged(Id.FullKey(), next.Version, next.SelectedPath));
        return next;
    }

    public async Task<PropertyGridState> SetValue(string path, string value)
    {
        if (State.ReadOnly)
        {
            throw new InvalidOperationException("PropertyGrid is read-only.");
        }
        var idx = Array.FindIndex(State.Entries, e => string.Equals(e.Path, path, StringComparison.Ordinal));
        if (idx < 0)
        {
            throw new ArgumentException($"Unknown property path '{path}'.", nameof(path));
        }
        var updated = State.Entries.ToArray();
        updated[idx] = updated[idx] with { Value = value ?? "" };
        var next = State with
        {
            Version = State.Version + 1,
            Entries = updated,
            SelectedPath = path,
        };
        await Save(next, new PropertyGridChanged(Id.FullKey(), next.Version, next.SelectedPath));
        return next;
    }

    public Task<PropertyGridState> Read() => Task.FromResult(State);

    private static PropertyGridEntry[] ParseJsonEntries(string raw)
    {
        var list = new List<PropertyGridEntry>();
        try
        {
            using var doc = JsonDocument.Parse(raw);
            VisitElement(doc.RootElement, list, parentPath: null, name: "root", path: "$", depth: 0, category: "State", isRoot: true);
        }
        catch (JsonException)
        {
            list.Add(new PropertyGridEntry("$", "value", raw, "string", null, 0, false, "Raw"));
        }
        return [.. list];
    }

    private static void VisitElement(JsonElement el, List<PropertyGridEntry> output, string? parentPath, string name, string path, int depth, string category, bool isRoot = false)
    {
        if (output.Count >= MaxEntries)
        {
            return;
        }

        switch (el.ValueKind)
        {
            case JsonValueKind.Object:
                var props = el.EnumerateObject().ToArray();
                if (!isRoot)
                {
                    output.Add(new PropertyGridEntry(path, name, string.Create(CultureInfo.InvariantCulture, $"{{{props.Length}}}"), "object", parentPath, depth, props.Length > 0, category));
                }
                foreach (var prop in props)
                {
                    if (output.Count >= MaxEntries) { break; }
                    var childPath = isRoot ? prop.Name : $"{path}.{prop.Name}";
                    var childParent = isRoot ? null : path;
                    var childCategory = isRoot ? (prop.Value.ValueKind is JsonValueKind.Object or JsonValueKind.Array ? prop.Name : "State") : category;
                    VisitElement(prop.Value, output, childParent, prop.Name, childPath, isRoot ? 0 : depth + 1, childCategory, isRoot: false);
                }
                if (isRoot && props.Length == 0)
                {
                    output.Add(new PropertyGridEntry("$", "(empty)", "{}", "object", null, 0, false, "State"));
                }
                break;

            case JsonValueKind.Array:
                var items = el.EnumerateArray().ToArray();
                output.Add(new PropertyGridEntry(path, name, string.Create(CultureInfo.InvariantCulture, $"[{items.Length}]"), "array", parentPath, depth, items.Length > 0, category));
                for (var i = 0; i < items.Length && output.Count < MaxEntries; i++)
                {
                    var indexName = string.Create(CultureInfo.InvariantCulture, $"[{i}]");
                    VisitElement(items[i], output, path, indexName, $"{path}{indexName}", depth + 1, category);
                }
                break;

            case JsonValueKind.String:
                output.Add(new PropertyGridEntry(path, name, el.GetString() ?? "", "string", parentPath, depth, false, category));
                break;

            case JsonValueKind.Number:
                output.Add(new PropertyGridEntry(path, name, el.GetRawText(), "number", parentPath, depth, false, category));
                break;

            case JsonValueKind.True:
            case JsonValueKind.False:
                output.Add(new PropertyGridEntry(path, name, el.GetBoolean() ? "true" : "false", "boolean", parentPath, depth, false, category));
                break;

            default:
                output.Add(new PropertyGridEntry(path, name, "null", "null", parentPath, depth, false, category));
                break;
        }
    }
}
