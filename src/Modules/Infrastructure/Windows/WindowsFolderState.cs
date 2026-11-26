namespace IntoChat.OS.Modules.Microsoft.Windows;

[GenerateSerializer, Alias("windows.folder-state")]
internal sealed record WindowsFolderState
{
    [Id(0)]
    public WindowsFolderGrant[] Grants { get; init; } = [];
}
