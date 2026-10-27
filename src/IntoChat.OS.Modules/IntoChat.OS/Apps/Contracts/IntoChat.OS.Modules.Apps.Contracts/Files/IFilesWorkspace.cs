using DigitalBrain.Contracts;

namespace IntoChat.OS.Modules.Apps;

[NeuronAlias("files.workspace"), DefaultNeuronType("apps.files")]
public interface IFilesWorkspace
{
    static string Key(string brain, string app = "files") => $"{brain}/apps/{app}/workspace";
    Task Open(CancellationToken ct = default);
    [DigitalBrain.ReadOnlyCall] Task<long> Revision();
    Task Request(string operation, FilesWorkspaceInput input, long expectedRevision, Guid requestId, CancellationToken ct = default);
}

[GenerateSerializer, Alias("files.workspace-input")]
public sealed record FilesWorkspaceInput(
    [property: Id(0)] int? Index = null,
    [property: Id(1)] string? Name = null,
    [property: Id(2)] string? Text = null,
    [property: Id(3)] string? Query = null,
    [property: Id(4)] bool Everywhere = false);
