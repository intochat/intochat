namespace DigitalBrain.Framework.Enforcement;

// Reserved for trusted platform code: left out of the program catalog, refused by the program
// edge, because a program could otherwise forge the caller context such a contract trusts.
// Scheduled to go once module contracts are declared rather than discovered.
[AttributeUsage(AttributeTargets.Interface, AllowMultiple = false, Inherited = false)]
public sealed class PlatformOnlyAttribute : Attribute
{
    // Marked itself, inheriting a marked interface, or declared in a platform assembly.
    public static bool AppliesTo(Type contract)
    {
        ArgumentNullException.ThrowIfNull(contract);
        return PlatformAssemblyAttribute.IsPlatform(contract.Assembly)
            || contract.IsDefined(typeof(PlatformOnlyAttribute), inherit: false)
            || contract.GetInterfaces().Any(inherited => inherited.IsDefined(typeof(PlatformOnlyAttribute), inherit: false));
    }
}
