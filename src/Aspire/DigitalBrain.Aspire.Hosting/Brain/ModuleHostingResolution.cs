using System.Reflection;
using DigitalBrain.Contracts;

namespace DigitalBrain.Aspire.Hosting;

public static class ModuleHostingResolution
{
    // A missing assembly is a composition without that module's infrastructure, not an error;
    // a loadable assembly without the named type, or a type that is not an adapter, is a bug
    // in the attribute and must fail loudly.
    public static IDigitalBrainModuleHosting? Resolve(Type moduleType)
    {
        ArgumentNullException.ThrowIfNull(moduleType);
        if (moduleType.GetCustomAttribute<ModuleHostingAttribute>() is not { } attribute) { return null; }
        var parts = attribute.TypeName.Split(',', 2);
        if (parts.Length != 2)
        { throw new InvalidOperationException($"[ModuleHosting] on {moduleType.Name} must name 'Type, Assembly'."); }
        Assembly assembly;
        try { assembly = Assembly.Load(parts[1].Trim()); }
        catch (FileNotFoundException) { return null; }
        catch (FileLoadException) { return null; }
        var hostingType = assembly.GetType(parts[0].Trim())
            ?? throw new InvalidOperationException($"'{attribute.TypeName}' on {moduleType.Name} names a type that is not in {assembly.GetName().Name}.");
        if (!typeof(IDigitalBrainModuleHosting).IsAssignableFrom(hostingType))
        { throw new InvalidOperationException($"'{attribute.TypeName}' on {moduleType.Name} is not an {nameof(IDigitalBrainModuleHosting)}."); }
        return (IDigitalBrainModuleHosting)Activator.CreateInstance(hostingType)!;
    }
}
