using System.Text.RegularExpressions;
using IntoChat.OS;
using IntoChat.OS.Flutter;
using IntoChat.OS.Flutter.Calendar.Signals;
using IntoChat.OS.Framework;
using Orleans.Concurrency;
using Orleans.Runtime;

namespace IntoChat.OS.Flutter.Calendar;

[GrainType(UIVocabulary.CalendarType)]
internal sealed class CalendarNeuron([PersistentState("state", IntoChatOSNames.ClusterGrainStorage)] IPersistentState<CalendarState> store)
    : Neuron<CalendarState>(store), ICalendar
{
    public Task Set(string mode, IReadOnlyList<string> selected)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(mode);
        ArgumentNullException.ThrowIfNull(selected);
        var next = Snapshot;
        next.Name = this.GetPrimaryKeyString();
        next.Version++;
        next.Mode = mode.Trim();
        next.Selected = [.. selected];
        return Save(next, new CalendarChanged(this.GetPrimaryKeyString(), next.Version));
    }

    [ReadOnly] public Task<CalendarState> Read() { Snapshot.Name = this.GetPrimaryKeyString(); return Task.FromResult(Snapshot); }
}
