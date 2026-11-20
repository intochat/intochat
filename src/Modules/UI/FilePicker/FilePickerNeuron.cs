using IntoChat.OS.Modules.UI.Core;
using DigitalBrain.Neurons;
using IntoChat.OS.Modules.UI.FilePicker.Signals;

namespace IntoChat.OS.Modules.UI.FilePicker;

[Neuron("ui.file-picker")]
internal sealed class FilePickerNeuron : UiNeuronBase<FilePickerState>, IFilePicker
{
    public async Task<FilePickerState> Configure(
        string title,
        string currentPath,
        IReadOnlyList<FilePickerEntry> entries,
        IReadOnlyList<string>? filters = null,
        bool allowMultiple = false,
        string mode = "open")
    {
        ArgumentNullException.ThrowIfNull(entries);
        var filterArr = filters is { Count: > 0 } ? filters.Select(f => f.Trim()).Where(f => f.Length > 0).ToArray() : ["*"];
        if (filterArr.Length == 0) { filterArr = ["*"]; }

        var normMode = (mode ?? "open").Trim().ToLowerInvariant() switch
        {
            "save" => "save",
            "folder" => "folder",
            _ => "open",
        };

        var next = State with
        {
            Version = State.Version + 1,
            Title = string.IsNullOrWhiteSpace(title) ? "Select file" : title.Trim(),
            Mode = normMode,
            CurrentPath = NormalizePath(currentPath),
            Entries = [.. entries],
            Filters = filterArr,
            ActiveFilter = filterArr[0],
            SelectedPaths = [],
            AllowMultiple = allowMultiple,
            ConfirmedPath = null,
        };
        await Save(next, new FilePickerChanged(Id.FullKey(), next.Version, next.CurrentPath, next.ConfirmedPath));
        return next;
    }

    public async Task<FilePickerState> SetEntries(string currentPath, IReadOnlyList<FilePickerEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        var next = State with
        {
            Version = State.Version + 1,
            CurrentPath = NormalizePath(currentPath),
            Entries = [.. entries],
            SelectedPaths = [],
        };
        await Save(next, new FilePickerChanged(Id.FullKey(), next.Version, next.CurrentPath, next.ConfirmedPath));
        return next;
    }

    public async Task<FilePickerState> SetFilter(string filter)
    {
        var normalized = string.IsNullOrWhiteSpace(filter) ? "*" : filter.Trim();
        var next = State with
        {
            Version = State.Version + 1,
            ActiveFilter = normalized,
        };
        await Save(next, new FilePickerChanged(Id.FullKey(), next.Version, next.CurrentPath, next.ConfirmedPath));
        return next;
    }

    public async Task<FilePickerState> Select(string path)
    {
        var entry = State.Entries.FirstOrDefault(e => string.Equals(e.Path, path, StringComparison.Ordinal))
            ?? throw new ArgumentException($"Unknown file picker entry '{path}'.", nameof(path));

        if (!MatchesFilter(entry, State.ActiveFilter))
        {
            throw new InvalidOperationException($"Entry '{entry.Name}' does not match active filter '{State.ActiveFilter}'.");
        }

        string[] nextSelected;
        if (State.AllowMultiple)
        {
            var set = State.SelectedPaths.ToHashSet(StringComparer.Ordinal);
            if (!set.Remove(entry.Path)) { set.Add(entry.Path); }
            nextSelected = [.. set];
        }
        else
        {
            nextSelected = [entry.Path];
        }

        var next = State with
        {
            Version = State.Version + 1,
            SelectedPaths = nextSelected,
        };
        await Save(next, new FilePickerChanged(Id.FullKey(), next.Version, next.CurrentPath, next.ConfirmedPath));
        return next;
    }

    public async Task<FilePickerState> Confirm()
    {
        if (State.SelectedPaths.Length == 0 && State.Mode != "folder")
        {
            throw new InvalidOperationException("No file selected.");
        }

        var chosenPath = State.SelectedPaths.Length > 0 ? State.SelectedPaths[0] : State.CurrentPath;
        var entry = State.Entries.FirstOrDefault(e => string.Equals(e.Path, chosenPath, StringComparison.Ordinal));
        var confirmed = entry?.NeuronUri ?? chosenPath;

        var next = State with
        {
            Version = State.Version + 1,
            ConfirmedPath = confirmed,
        };
        await Save(next, new FilePickerChanged(Id.FullKey(), next.Version, next.CurrentPath, next.ConfirmedPath));
        return next;
    }

    public Task<FilePickerState> Read() => Task.FromResult(State);

    private static string NormalizePath(string? raw)
        => string.IsNullOrWhiteSpace(raw) ? "/" : raw.Trim().Replace('\\', '/');

    private static bool MatchesFilter(FilePickerEntry entry, string filter)
    {
        if (string.IsNullOrWhiteSpace(filter) || filter is "*" or "*.*")
        {
            return true;
        }
        var trimmed = filter.Trim();
        if (trimmed.StartsWith("*.", StringComparison.Ordinal) || trimmed.StartsWith('.'))
        {
            if (entry.IsDirectory)
            {
                return true;
            }
            var ext = trimmed.StartsWith("*.", StringComparison.Ordinal) ? trimmed[1..] : trimmed;
            return entry.Name.EndsWith(ext, StringComparison.OrdinalIgnoreCase);
        }
        return entry.Name.Contains(trimmed, StringComparison.OrdinalIgnoreCase)
            || entry.Path.Contains(trimmed, StringComparison.OrdinalIgnoreCase);
    }
}
