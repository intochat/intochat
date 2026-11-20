using DigitalBrain;

namespace IntoChat.OS.Modules.UI.Table.Signals;

[Signal("ui.table-changed")]
public sealed record UiTableChanged(
    string TableId,
    int Version,
    string Filter,
    string? SortBy,
    bool Descending,
    int Page) : Signal;
