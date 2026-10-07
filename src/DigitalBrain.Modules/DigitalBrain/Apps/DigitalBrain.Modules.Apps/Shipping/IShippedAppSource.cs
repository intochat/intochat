namespace DigitalBrain.Apps;

// Shipped apps are ordinary share bundles the image carries. Publisher is a vendor name the
// catalog lists them under, never resolved to a principal.
public interface IShippedAppSource
{
    string Publisher { get; }
    // Development folders accept the publisher's published versions back.
    bool Writable { get; }
    IReadOnlyList<AppBundle> Load();
    void Write(AppBundle bundle);
}
