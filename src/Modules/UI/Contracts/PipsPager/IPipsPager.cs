using DigitalBrain;
using DigitalBrain.Contracts;

namespace IntoChat.OS.Modules.UI.PipsPager;

[GenerateSerializer, Alias("ui.pips-pager.state")]
public sealed record PipsPagerState(
    [property: Id(0)] int Version,
    [property: Id(1)] int PageCount,
    [property: Id(2)] int SelectedIndex,
    [property: Id(3)] int MaxVisiblePips,
    [property: Id(4)] string Orientation,
    [property: Id(5)] bool ShowPrevNext)
{
    public PipsPagerState() : this(0, 1, 0, 5, "horizontal", true) { }
}

[NeuronAlias("ui.pips-pager")]
[DefaultNeuronType("ui.pips-pager")]
public interface IPipsPager
{
    Task<PipsPagerState> Configure(int pageCount, int selectedIndex = 0, int maxVisiblePips = 5, string orientation = "horizontal", bool showPrevNext = true);
    Task<PipsPagerState> SelectPage(int index);
    Task<PipsPagerState> Next();
    Task<PipsPagerState> Previous();
    [ReadOnlyCall] Task<PipsPagerState> Read();
}
