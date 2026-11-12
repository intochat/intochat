using DigitalBrain.Contracts;

namespace IntoChat.OS.Modules.Apps;

[NeuronAlias("files.workspace"), DefaultNeuronType("apps.files")]
public interface IFilesWorkspace
{
    static string Key(string brain, string app = "files") => $"{brain}/apps/{app}/workspace";
    Task Open(CancellationToken ct = default);
    [DigitalBrain.Contracts.ReadOnlyCall] Task<long> Revision();
    Task Request(string operation, FilesWorkspaceInput input, long expectedRevision, Guid requestId, CancellationToken ct = default);
}

public sealed record FilesWorkspaceInput(
    int? Index = null,
    string? Name = null,
    string? Text = null,
    string? Query = null,
    bool Everywhere = false);
