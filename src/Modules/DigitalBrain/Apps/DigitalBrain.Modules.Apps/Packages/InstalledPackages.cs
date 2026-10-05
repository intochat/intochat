using DigitalBrain.Apps;
using DigitalBrain.Enforcement;
using DigitalBrain.Kernel.Enforcement;

namespace DigitalBrain.Apps;

public static class InstalledPackages
{
    public static string AppKey(PackageId id) => BrainScope.CurrentId() + "/packages/" + id;
}

