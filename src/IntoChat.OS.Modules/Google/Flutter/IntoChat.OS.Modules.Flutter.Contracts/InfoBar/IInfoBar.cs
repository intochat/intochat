using DigitalBrain.Contracts;
using DigitalBrain;
using IntoChat.OS;
using DigitalBrain.State;

namespace IntoChat.OS.Modules.Flutter.InfoBar;

[NeuronAlias("infobar"), DefaultNeuronType(UIVocabulary.InfoBarType)]
public interface IInfoBar
{
    Task Show(string severity, string title, string body);
    Task Dismiss();
    [ReadOnlyCall] Task<InfoBarState> Read();
}

[GenerateSerializer, Alias("ui.infobar-state")]
[ExportableState]
public sealed class InfoBarState
{
    [Id(0)] public string Name { get; set; } = "";
    [Id(1)] public int Version { get; set; }
    [Id(2)] public string Severity { get; set; } = "info";
    [Id(3)] public string Title { get; set; } = "";
    [Id(4)] public string Body { get; set; } = "";
    [Id(5)] public bool Visible { get; set; }
}
