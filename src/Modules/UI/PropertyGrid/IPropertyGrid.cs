using IntoChat.OS.Modules.UI.Core;
using DigitalBrain;
using DigitalBrain.Contracts;

namespace IntoChat.OS.Modules.UI.PropertyGrid;

[GenerateSerializer, Alias("ui.property-grid.entry")]
public sealed record PropertyGridEntry(
    [property: Id(0)] string Path,
    [property: Id(1)] string Name,
    [property: Id(2)] string Value,
    [property: Id(3)] string ValueKind = "string",
    [property: Id(4)] string? ParentPath = null,
    [property: Id(5)] int Depth = 0,
    [property: Id(6)] bool HasChildren = false,
    [property: Id(7)] string Category = "General")
{
    public PropertyGridEntry() : this("", "", "", "string", null, 0, false, "General") { }
}

[GenerateSerializer, Alias("ui.property-grid.state")]
public sealed record PropertyGridState(
    [property: Id(0)] int Version,
    [property: Id(1)] string Title,
    [property: Id(2)] PropertyGridEntry[] Entries,
    [property: Id(3)] string[] ExpandedPaths,
    [property: Id(4)] string? SelectedPath,
    [property: Id(5)] string Filter,
    [property: Id(6)] bool ReadOnly,
    [property: Id(7)] string RawJson)
{
    public PropertyGridState() : this(0, "Properties", [], [], null, "", true, "{}") { }
}

[NeuronAlias("ui.property-grid")]
[DefaultNeuronType("ui.property-grid")]
public interface IPropertyGrid : IUiInteractiveControl
{
    Task<PropertyGridState> Configure(string title, IReadOnlyList<PropertyGridEntry> entries, bool readOnly = true);
    Task<PropertyGridState> LoadJson(string title, string json, bool readOnly = true);
    Task<PropertyGridState> ToggleExpand(string path);
    Task<PropertyGridState> Select(string path);
    Task<PropertyGridState> SetFilter(string filter);
    Task<PropertyGridState> SetValue(string path, string value);
    [ReadOnlyCall] Task<PropertyGridState> Read();
}
