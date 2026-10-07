using DigitalBrain.Framework;

namespace DigitalBrain.Microsoft.CSharp.Catalog;

[GrainType("csharp.catalog")]
internal sealed class CSharpCatalogNeuron([PersistentState("catalog", DigitalBrainNames.DefaultGrainStorage)] IPersistentState<CSharpCatalogState> state)
    : Neuron, ICSharpCatalog
{
    private const int MaxFilesPerWorkspace = 500;

    public async Task<CSharpDescription> Describe(string id, string? name, string? purpose)
    {
        var files = state.State.Files;
        if (!files.TryGetValue(id, out var current))
        {
            if (files.Count >= MaxFilesPerWorkspace)
            { throw new InvalidOperationException($"This workspace has reached its {MaxFilesPerWorkspace}-file limit."); }
            current = new(id, id, "", DateTimeOffset.UtcNow);
        }
        var described = current with
        {
            Name = string.IsNullOrWhiteSpace(name) ? current.Name : name.Trim(),
            Purpose = purpose?.Trim() ?? current.Purpose,
        };
        files[id] = described;
        await state.WriteStateAsync();
        return described;
    }

    public Task<CSharpDescription[]> List()
        => Task.FromResult(state.State.Files.Values.OrderBy(file => file.Name, StringComparer.OrdinalIgnoreCase).ToArray());

    public Task<CSharpDescription> Read(string id)
        => Task.FromResult(state.State.Files.TryGetValue(id, out var file) ? file
            : throw new KeyNotFoundException($"{id} is not a C# file in this workspace."));

    public async Task Remove(string id)
    {
        if (!state.State.Files.Remove(id)) { return; }
        await state.WriteStateAsync();
    }
}
