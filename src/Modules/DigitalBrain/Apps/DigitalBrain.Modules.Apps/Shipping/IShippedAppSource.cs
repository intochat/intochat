namespace DigitalBrain.Apps;

// Shipped apps are ordinary share bundles the host imports at startup into the publisher
// account's own brain.
public interface IShippedAppSource
{
    string Account { get; }
    // Bundles compiled into the host passed their tests.cs in CI; the host does not rerun them.
    // Folder sources are verified at startup.
    bool VerifiedByBuild { get; }
    // Development folders accept the publisher's published versions back.
    bool Writable { get; }
    IReadOnlyList<AppBundle> Load();
    void Write(AppBundle bundle);
}
