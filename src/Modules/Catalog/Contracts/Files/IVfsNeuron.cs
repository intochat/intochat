using DigitalBrain.Contracts;

namespace IntoChat.OS.Modules.Apps;

/// <summary>
/// Marks a neuron contract interface as eligible for cross-brain sharing via Grants.
/// </summary>
[AttributeUsage(AttributeTargets.Interface, Inherited = false, AllowMultiple = false)]
public sealed class ShareableAttribute : Attribute
{
}

public static class VfsNeuronDefaults
{
    public const string TypeName = "files.vfs";
    public static string Key(string brain, string workspace = "default") => $"{brain}/files/{workspace}";
}

[NeuronAlias(VfsNeuronDefaults.TypeName), DefaultNeuronType(VfsNeuronDefaults.TypeName), Shareable]
public interface IVfsNeuron : IFilesNeuron
{
}

[Shareable]
public interface IFilesNeuron
{
    Task<WorkspaceFileEntry[]> ListFiles(string? query = null);
    Task<WorkspaceFileEntry?> ResolveFile(string nameOrUriOrPath);
    Task<string?> ReadText(string nameOrUriOrPath);
    Task<byte[]?> ReadBytes(string nameOrUriOrPath);
    Task<WorkspaceFileEntry> WriteFile(string fileNameOrPath, byte[] content);
    Task<WorkspaceFileEntry> WriteText(string fileNameOrPath, string content);
}
