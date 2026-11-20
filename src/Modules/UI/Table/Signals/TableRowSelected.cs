using DigitalBrain;

namespace IntoChat.OS.Modules.UI.Table.Signals;

[Signal("ui.table-row-selected")]
public sealed record TableRowSelected(
    string TableId,
    int? RowIndex) : Signal;
