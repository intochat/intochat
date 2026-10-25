using System.Text.RegularExpressions;
using IntoChat.OS;
using IntoChat.OS.Modules.Flutter;
using IntoChat.OS.Modules.Flutter.InfoBar.Signals;
using IntoChat.OS.Contracts.Configuration;
using IntoChat.OS.Framework;
using Orleans.Concurrency;
using Orleans.Runtime;

namespace IntoChat.OS.Modules.Flutter.InfoBar;

[GrainType(UIVocabulary.InfoBarType)]
internal sealed class InfoBarNeuron([PersistentState("state", IntoChatOSNames.ClusterGrainStorage)] IPersistentState<InfoBarState> store)
    : Neuron<InfoBarState>(store), IInfoBar
{
    public Task Show(string severity, string title, string body)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(severity);
        ArgumentException.ThrowIfNullOrWhiteSpace(title);
        ArgumentNullException.ThrowIfNull(body);
        var next = Snapshot;
        next.Name = this.GetPrimaryKeyString();
        next.Version++;
        next.Severity = severity.Trim();
        next.Title = title.Trim();
        next.Body = body;
        next.Visible = true;
        return Save(next, new InfoBarChanged(this.GetPrimaryKeyString(), true));
    }

    public Task Dismiss()
    {
        var next = Snapshot;
        next.Name = this.GetPrimaryKeyString();
        next.Version++;
        next.Visible = false;
        return Save(next, new InfoBarChanged(this.GetPrimaryKeyString(), false), new InfoBarDismissed(this.GetPrimaryKeyString()));
    }

    [ReadOnly] public Task<InfoBarState> Read() { Snapshot.Name = this.GetPrimaryKeyString(); return Task.FromResult(Snapshot); }
}
