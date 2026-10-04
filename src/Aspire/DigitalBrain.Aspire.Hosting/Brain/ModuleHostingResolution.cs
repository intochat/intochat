using System.Reflection;
using DigitalBrain.Contracts;

namespace DigitalBrain.Aspire.Hosting;

public static class ModuleHostingResolution
{
    // A missing assembly is a composition without that module's infrastructure, not an error;
    // a present type that is not an adapter is a bug in the attribute.
    public static IDigitalBrainModuleHosting? Resolve(Type moduleType)
    {
        ArgumentNullException.ThrowIfNull(moduleType);
        if (moduleType.GetCustomAttribute<ModuleHostingAttribute>() is not { } attribute) { return null; }
        if (Type.GetType(attribute.TypeName, throwOnError: false) is not { } hostingType) { return null; }
        if (!typeof(IDigitalBrainModuleHosting).IsAssignableFrom(hostingType))
        { throw new InvalidOperationException($"'{attribute.TypeName}' on {moduleType.Name} is not an {nameof(IDigitalBrainModuleHosting)}."); }
        return (IDigitalBrainModuleHosting)Activator.CreateInstance(hostingType)!;
    }
}
