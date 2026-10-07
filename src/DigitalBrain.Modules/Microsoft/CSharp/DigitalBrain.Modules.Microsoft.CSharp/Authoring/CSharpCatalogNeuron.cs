using DigitalBrain.Framework;
using DigitalBrain.Framework.Enforcement;

namespace DigitalBrain.Microsoft.CSharp;

[GrainType("csharp.catalog")]
internal sealed class CSharpCatalogNeuron([PersistentState("catalog", DigitalBrainNames.DefaultGrainStorage)] IPersistentState<CSharpCatalogState> state)
    : Neuron, ICSharpCatalog
{
    private const int MaxFilesPerWorkspace = 500;

    // The key's first segment is the brain; the catalog belongs to it whatever the id looks like.
    public override NeuronAccess Access(string operation)
    {
        var key = this.GetPrimaryKeyString();
        var scope = key.LastIndexOf("/csharp/catalog", StringComparison.Ordinal) is var at and > 0 ? key[..at] : key;
        return new NeuronAccess(scope);
    }

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
        await Write(new Dictionary<string, CSharpDescription>(files, StringComparer.Ordinal) { [id] = described });
        return described;
    }

    public Task<CSharpDescription[]> List()
        => Task.FromResult(state.State.Files.Values.OrderBy(file => file.Name, StringComparer.OrdinalIgnoreCase).ToArray());

    public Task<CSharpDescription> Read(string id)
        => Task.FromResult(state.State.Files.TryGetValue(id, out var file) ? file
            : throw new KeyNotFoundException($"{id} is not a C# file in this workspace."));

    public async Task Remove(string id)
    {
        if (!state.State.Files.ContainsKey(id)) { return; }
        var files = new Dictionary<string, CSharpDescription>(state.State.Files, StringComparer.Ordinal);
        files.Remove(id);
        await Write(files);
    }

    private async Task Write(Dictionary<string, CSharpDescription> files)
    {
        var previous = state.State;
        state.State = new CSharpCatalogState { Files = files };
        try { await state.WriteStateAsync(); }
        catch { state.State = previous; throw; }
    }
}
