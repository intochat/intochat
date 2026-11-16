using DigitalBrain.Contracts;

namespace IntoChat.OS.Modules.Apps;

public static class VfsNeuronDefaults
{
    public const string TypeName = "files.vfs";
    public static string Key(string brain, string workspace = "default") => $"{brain}/files/{workspace}";
}

[NeuronAlias(VfsNeuronDefaults.TypeName), DefaultNeuronType(VfsNeuronDefaults.TypeName)]
public interface IVfsNeuron : IFilesNeuron
{
}

// A brain's files are reached from another brain only through IFileShares, never through this contract.
public interface IFilesNeuron
{
    Task<WorkspaceFileEntry[]> ListFiles(string? query = null);
    Task<WorkspaceFileEntry?> ResolveFile(string nameOrUriOrPath);
    Task<string?> ReadText(string nameOrUriOrPath);
    Task<byte[]?> ReadBytes(string nameOrUriOrPath);
    Task<WorkspaceFileEntry> WriteFile(string fileNameOrPath, byte[] content);
    Task<WorkspaceFileEntry> WriteText(string fileNameOrPath, string content);
}
