using DigitalBrain;

namespace IntoChat.OS.Modules.UI.PipsPager.Signals;

[Signal("ui.pips-pager-changed")]
public sealed record PipsPagerChanged(string PagerId, int Version, int SelectedIndex, int PageCount) : Signal;
