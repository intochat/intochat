using System.Reflection;

namespace DigitalBrain.Framework.Contracts;

// Marks the platform ring: nothing in such an assembly is ever a program-visible contract.
// Scheduled to go together with PlatformOnlyAttribute.
[AttributeUsage(AttributeTargets.Assembly, AllowMultiple = false)]
public sealed class PlatformAssemblyAttribute : Attribute
{
    public static bool IsPlatform(Assembly assembly)
    {
        ArgumentNullException.ThrowIfNull(assembly);
        return assembly.IsDefined(typeof(PlatformAssemblyAttribute), inherit: false);
    }
}
