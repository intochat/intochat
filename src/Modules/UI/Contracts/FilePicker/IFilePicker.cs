using DigitalBrain;
using DigitalBrain.Contracts;

namespace IntoChat.OS.Modules.UI.FilePicker;

[GenerateSerializer, Alias("ui.file-picker.entry")]
public sealed record FilePickerEntry(
    [property: Id(0)] string Path,
    [property: Id(1)] string Name,
    [property: Id(2)] bool IsDirectory = false,
    [property: Id(3)] long SizeBytes = 0,
    [property: Id(4)] string? NeuronUri = null)
{
    public FilePickerEntry() : this("", "", false, 0, null) { }
}

[GenerateSerializer, Alias("ui.file-picker.state")]
public sealed record FilePickerState(
    [property: Id(0)] int Version,
    [property: Id(1)] string Title,
    [property: Id(2)] string Mode,
    [property: Id(3)] string CurrentPath,
    [property: Id(4)] FilePickerEntry[] Entries,
    [property: Id(5)] string[] Filters,
    [property: Id(6)] string ActiveFilter,
    [property: Id(7)] string[] SelectedPaths,
    [property: Id(8)] bool AllowMultiple,
    [property: Id(9)] string? ConfirmedPath)
{
    public FilePickerState() : this(0, "Select file", "open", "/", [], ["*"], "*", [], false, null) { }
}

[NeuronAlias("ui.file-picker")]
[DefaultNeuronType("ui.file-picker")]
public interface IFilePicker
{
    Task<FilePickerState> Configure(
        string title,
        string currentPath,
        IReadOnlyList<FilePickerEntry> entries,
        IReadOnlyList<string>? filters = null,
        bool allowMultiple = false,
        string mode = "open");
    Task<FilePickerState> SetEntries(string currentPath, IReadOnlyList<FilePickerEntry> entries);
    Task<FilePickerState> SetFilter(string filter);
    Task<FilePickerState> Select(string path);
    Task<FilePickerState> Confirm();
    [ReadOnlyCall] Task<FilePickerState> Read();
}
