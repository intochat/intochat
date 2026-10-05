namespace DigitalBrain.Apps;

// Shipped apps are ordinary share bundles the host verifies once and imports at startup into
// the publisher account's own brain.
public interface IShippedAppSource
{
    string Account { get; }
    IReadOnlyList<AppBundle> Load();
}
