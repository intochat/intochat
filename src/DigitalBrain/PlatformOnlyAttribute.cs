namespace DigitalBrain;

/// <summary>
/// Marks a neuron contract as reserved for trusted platform code: it is left out of the catalog
/// programs see, and the program edge refuses to invoke it.
/// </summary>
/// <remarks>
/// Exists because a program could otherwise forge the caller context such a contract trusts.
/// Enforcement, not vocabulary: scheduled to leave this ring once module contracts are declared
/// rather than discovered.
/// </remarks>
[AttributeUsage(AttributeTargets.Interface, AllowMultiple = false, Inherited = false)]
public sealed class PlatformOnlyAttribute : Attribute
{
    /// <summary>
    /// Whether <paramref name="contract"/> is platform-only: marked itself, inheriting a marked
    /// interface, or declared in a <see cref="PlatformAssemblyAttribute"/> assembly.
    /// </summary>
    public static bool AppliesTo(Type contract)
    {
        ArgumentNullException.ThrowIfNull(contract);
        return PlatformAssemblyAttribute.IsPlatform(contract.Assembly)
            || contract.IsDefined(typeof(PlatformOnlyAttribute), inherit: false)
            || contract.GetInterfaces().Any(inherited => inherited.IsDefined(typeof(PlatformOnlyAttribute), inherit: false));
    }
}
