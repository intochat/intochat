using DigitalBrain.Contracts;
using DigitalBrain;
using IntoChat.OS;
using DigitalBrain.State;

namespace IntoChat.OS.Modules.Flutter.Calendar;

[NeuronAlias("calendar"), DefaultNeuronType(UIVocabulary.CalendarType)]
public interface ICalendar
{
    Task Set(string mode, IReadOnlyList<string> selected);
    [DigitalBrain.ReadOnlyCall] Task<CalendarState> Read();
}

[GenerateSerializer, Alias("ui.calendar-state")]
[ExportableState]
public sealed class CalendarState
{
    [Id(0)] public string Name { get; set; } = "";
    [Id(1)] public int Version { get; set; }
    [Id(2)] public string Mode { get; set; } = "day";
    [Id(3)] public List<string> Selected { get; set; } = [];
}
