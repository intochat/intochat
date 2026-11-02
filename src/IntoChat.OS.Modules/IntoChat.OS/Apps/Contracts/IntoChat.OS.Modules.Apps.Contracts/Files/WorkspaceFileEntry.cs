namespace IntoChat.OS.Modules.Apps;

public sealed record WorkspaceFileEntry(
    string Name,
    string Connection,
    string ItemId,
    string Kind,
    long? Size,
    string NeuronUri,
    string VfsPath,
    DateTimeOffset? Modified = null);
