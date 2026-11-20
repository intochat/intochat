using IntoChat.OS.Modules.UI.Core;
using DigitalBrain;
using DigitalBrain.Contracts;

namespace IntoChat.OS.Modules.UI.TeachingTip;

[GenerateSerializer, Alias("ui.teaching-tip.state")]
public sealed record TeachingTipState(
    [property: Id(0)] int Version,
    [property: Id(1)] string Title,
    [property: Id(2)] string Subtitle,
    [property: Id(3)] bool IsOpen,
    [property: Id(4)] string? ActionLabel,
    [property: Id(5)] int ActionCount)
{
    public TeachingTipState() : this(0, "Tip", "", false, null, 0) { }
}

[NeuronAlias("ui.teaching-tip")]
[DefaultNeuronType("ui.teaching-tip")]
public interface ITeachingTip : IUiInteractiveControl
{
    Task<TeachingTipState> Show(string title, string subtitle, string? actionLabel = null);
    Task<TeachingTipState> InvokeAction();
    Task<TeachingTipState> Dismiss();
    [ReadOnlyCall] Task<TeachingTipState> Read();
}
