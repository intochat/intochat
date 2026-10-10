
namespace DigitalBrain.Framework.Compute;

// Modules price what they own. Micros per one unit at list price, or null when unpriced here.
public interface IPriceList
{
    long? MicrosPerUnit(CostKind kind, string item);
}
