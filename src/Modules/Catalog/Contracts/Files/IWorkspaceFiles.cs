using DigitalBrain.Components;
using DigitalBrain.Contracts;

namespace IntoChat.OS.Modules.Apps;

// One file or folder of the brain's workspace. Path is the stored form ("/work/reports/q4.csv").
public sealed record WorkspaceFile(string Path, string Name, bool IsFolder, long? Size, DateTimeOffset? Modified);

public static class WorkspaceFilesDefaults
{
    public const string TypeName = "workspace.files";
    public const string Key = "workspace/files";
    public const long MaxReadBytes = 16 * 1024 * 1024;
    public static DigitalBrain.NeuronId Id(DigitalBrain.BrainId brain) => new(brain, TypeName, Key);
}

// The brain's workspace files by path ("reports/q4.csv", or the stored "/work/reports/q4.csv"), for a caller of this brain:
// a screen that declares it as a slot and is granted it, the brain's person, its agents and modules. A caller of another
// brain, or a call with no caller, is refused; a path outside the workspace ("/local/...", "..") is refused. A read is capped
// at MaxReadBytes; a write carries its bytes as a call argument, so the runtime's argument bound (1 MiB of call) caps it. A
// write goes into an existing folder and replaces a file of the same name.
[NeuronAlias(WorkspaceFilesDefaults.TypeName), DefaultNeuronType(WorkspaceFilesDefaults.TypeName)]
public interface IWorkspaceFiles
{
    // The file or folder at the path, or null.
    [ReadOnlyCall] Task<WorkspaceFile?> Find(string path);

    // The files and folders in the folder at the path ("" is the workspace itself); empty when there is no such folder.
    [ReadOnlyCall] Task<WorkspaceFile[]> List(string folder);

    // The bytes of the file at the path, or null when there is none.
    [ReadOnlyCall] Task<byte[]?> Read(string path);

    // Writes the file and returns it once it is on disk.
    Task<WorkspaceFile> Write(string path, byte[] content);
}
