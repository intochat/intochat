using DigitalBrain.Contracts;

namespace IntoChat.OS.Modules.Apps;

// Compiled apps keep durable records in a small store-backed neuron of their own; App<TState>.State is per-activation working
// state (persisting it directly is a framework fix-wave candidate).

// The Desktop's records, at {brain}/apps/desktop/records: the open widgets in the order opened, whether Ino has greeted the person
// (once per brain), and the chosen layout.
public sealed record DesktopRecords(string[] Widgets, bool Greeted, string Layout)
{
    public DesktopRecords() : this([], false, "") { }
}

[NeuronAlias("apps.desktop-records"), DefaultNeuronType("apps.desktop-records")]
public interface IDesktopRecords
{
    const string Key = "apps/desktop/records";

    [DigitalBrain.ReadOnlyCall] Task<DesktopRecords> Read();
    Task Write(DesktopRecords records);
}

[DigitalBrain.Signal("apps.desktop-records-changed")]
public sealed record DesktopRecordsChanged : DigitalBrain.Signal;

// Ino's records, at {brain}/apps/ino/records: the pasted links, newest last.
public sealed record InoRecords(InoLink[] Links)
{
    public InoRecords() : this([]) { }
}

// One pasted link; serialised as the chat's link store reads it (Request is the post's request id, "N" format).
public sealed record InoLink(string Request, string App, string Input);

[NeuronAlias("apps.ino-records"), DefaultNeuronType("apps.ino-records")]
public interface IInoRecords
{
    const string Key = "apps/ino/records";

    [DigitalBrain.ReadOnlyCall] Task<InoRecords> Read();
    Task Write(InoRecords records);
}

[DigitalBrain.Signal("apps.ino-records-changed")]
public sealed record InoRecordsChanged : DigitalBrain.Signal;
