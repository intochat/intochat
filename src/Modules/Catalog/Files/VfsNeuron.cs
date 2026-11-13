using DigitalBrain;
using DigitalBrain.Neurons;
using IntoChat.OS.Primitives;

namespace IntoChat.OS.Modules.Apps;

[GenerateSerializer, Alias("apps.vfs-state")]
public sealed record VfsState
{
    [Id(0)] public long Revision { get; init; }
}

[Neuron(VfsNeuronDefaults.TypeName)]
public sealed class VfsNeuron(IServiceProvider services) : Neuron<VfsState>, IVfsNeuron
{
    private IFilesVfsService? _vfs;
    private IFilesVfsService Vfs => _vfs ??= WorkspaceFilesService.ForBrain(services, Id.Brain.Value);

    public async Task<WorkspaceFileEntry[]> ListFiles(string? query = null)
    {
        var files = await Vfs.ListFilesAsync(query);
        return [.. files];
    }

    public Task<WorkspaceFileEntry?> ResolveFile(string nameOrUriOrPath)
    {
        if (string.IsNullOrWhiteSpace(nameOrUriOrPath) || nameOrUriOrPath.Contains('\0', StringComparison.Ordinal))
        {
            return Task.FromResult<WorkspaceFileEntry?>(null);
        }
        return Vfs.ResolveFileAsync(nameOrUriOrPath);
    }

    public Task<string?> ReadText(string nameOrUriOrPath)
    {
        if (string.IsNullOrWhiteSpace(nameOrUriOrPath) || nameOrUriOrPath.Contains('\0', StringComparison.Ordinal))
        {
            return Task.FromResult<string?>(null);
        }
        return Vfs.ReadTextAsync(nameOrUriOrPath);
    }

    public Task<byte[]?> ReadBytes(string nameOrUriOrPath)
    {
        if (string.IsNullOrWhiteSpace(nameOrUriOrPath) || nameOrUriOrPath.Contains('\0', StringComparison.Ordinal))
        {
            return Task.FromResult<byte[]?>(null);
        }
        return Vfs.ReadBytesAsync(nameOrUriOrPath);
    }

    public Task<WorkspaceFileEntry> WriteFile(string fileNameOrPath, byte[] content)
        => Vfs.WriteFileAsync(fileNameOrPath, content);

    public Task<WorkspaceFileEntry> WriteText(string fileNameOrPath, string content)
        => Vfs.WriteTextAsync(fileNameOrPath, content);
}
