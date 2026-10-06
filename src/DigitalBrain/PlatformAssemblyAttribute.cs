using System.Reflection;

namespace DigitalBrain;

/// <summary>
/// Marks a whole assembly as the platform ring: none of its contracts are ever visible to programs,
/// whatever their own attributes say. Applied once, in <c>DigitalBrain.Platform</c>.
/// </summary>
/// <remarks>
/// Enforcement, not vocabulary: scheduled to leave this ring together with
/// <see cref="PlatformOnlyAttribute"/>.
/// </remarks>
[AttributeUsage(AttributeTargets.Assembly, AllowMultiple = false)]
public sealed class PlatformAssemblyAttribute : Attribute
{
    /// <summary>Whether <paramref name="assembly"/> carries this attribute.</summary>
    public static bool IsPlatform(Assembly assembly)
    {
        ArgumentNullException.ThrowIfNull(assembly);
        return assembly.IsDefined(typeof(PlatformAssemblyAttribute), inherit: false);
    }
}
