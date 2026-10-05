namespace DigitalBrain.Apps;

// Shipped apps are ordinary share bundles the host verifies once and imports at startup.
public interface IShippedAppSource
{
    string Publisher { get; }
    IReadOnlyList<AppBundle> Load();
}
