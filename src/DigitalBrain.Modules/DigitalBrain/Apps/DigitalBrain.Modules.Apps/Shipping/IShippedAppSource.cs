namespace DigitalBrain.Apps;

// Shipped apps are ordinary share bundles the host imports at startup into the publisher
// account's own brain.
public interface IShippedAppSource
{
    string Account { get; }
    // Development folders accept the publisher's published versions back.
    bool Writable { get; }
    IReadOnlyList<AppBundle> Load();
    void Write(AppBundle bundle);
}
