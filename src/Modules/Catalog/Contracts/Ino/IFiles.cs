using DigitalBrain.Contracts;

namespace IntoChat.OS.Modules.Apps;

// One file write Ino started and has not yet reported back to its conversation.
public sealed record InoFileWrite(string Operation, string Conversation, string Name, string Folder, string? Outcome = null);

public sealed record InoFilesState(InoFileWrite[] Writes)
{
    public InoFilesState() : this([]) { }
}

[DigitalBrain.Signal("ino.files.write-requested")]
public sealed record InoFileWriteRequested(string Operation, string Conversation, string Name) : DigitalBrain.Signal;

[DigitalBrain.Signal("ino.files.write-reported")]
public sealed record InoFileWriteReported(string Operation, string Outcome) : DigitalBrain.Signal;

[NeuronAlias("files", Formerly = ["ino.files", "IntoChat.OS.Modules.Apps.IInoFiles"]), DefaultNeuronType("ino.files")]
public interface IFiles
{
    const string DefaultKey = "ino/files";

    [DigitalBrain.Contracts.ReadOnlyCall]
    Task<InoToolResult> Find(string pattern);

    [DigitalBrain.Contracts.ReadOnlyCall]
    Task<InoToolResult> Open(string path);

    [DigitalBrain.Contracts.ReadOnlyCall]
    Task<InoToolResult> Summarize(string path);

    /// <summary>
    /// Creates a new text file named <paramref name="name"/> in the owner's first writable local folder (Documents by default).
    /// The owner is asked to approve before anything is written; an existing file is never replaced.
    /// </summary>
    [System.ComponentModel.Description("Create a new text file in the owner's Documents folder. The owner approves it first. name is a file name only, e.g. hello.txt.")]
    Task<InoToolResult> Write(string name, string content);
}
