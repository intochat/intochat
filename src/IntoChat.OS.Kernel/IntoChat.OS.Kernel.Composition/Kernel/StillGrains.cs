using System.Collections.Frozen;
using System.Reflection;

namespace IntoChat.OS.Kernel.Composition;

// Transitional (06-09, D-04): IStillGrains. A contract passes when a concrete class deriving Orleans Grain outside the kernel
// composition implements it, in a loaded IntoChat assembly; NeuronsStillGrainsOnlyShrinks pins that set of classes to the
// allow-list. Orleans' own interfaces, the core os contracts every grain shares (INeuron) and DigitalBrain's host grains are
// refused, so the accessor cannot reach a plain neuron's host or a kernel grain.
internal sealed class StillGrains(IGrainFactory grains) : IntoChat.OS.Runtime.IStillGrains
{
    private static FrozenSet<Type> contracts = Scan();

    public object Get(Type contract, string key)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);
        return grains.GetGrain(Require(contract), key);
    }

    public object Get(Type contract, long key) => grains.GetGrain(Require(contract), key);

    internal static bool IsAllowListed(Type contract)
    {
        ArgumentNullException.ThrowIfNull(contract);
        if (contracts.Contains(contract)) { return true; }
        // An assembly loaded after the first scan (a module or test assembly) may bring its still-grain later.
        contracts = Scan();
        return contracts.Contains(contract);
    }

    private static Type Require(Type contract)
        => IsAllowListed(contract) ? contract
            : throw new InvalidOperationException($"{contract.FullName} is not served by an allow-listed still-grain (NeuronsStillGrains.json).");

    private static FrozenSet<Type> Scan()
        => AppDomain.CurrentDomain.GetAssemblies()
            .Where(assembly => !assembly.IsDynamic && assembly.GetName().Name?.StartsWith("IntoChat.", StringComparison.Ordinal) == true)
            .SelectMany(Loadable)
            .Where(type => type is { IsClass: true, IsAbstract: false } && typeof(Grain).IsAssignableFrom(type)
                && type.Namespace?.StartsWith("IntoChat.OS.Kernel.Composition", StringComparison.Ordinal) != true)
            .SelectMany(type => type.GetInterfaces())
            .Where(contract => contract.Assembly != typeof(IGrain).Assembly && contract.Assembly != typeof(INeuron).Assembly
                && contract.Assembly.GetName().Name?.StartsWith("IntoChat.", StringComparison.Ordinal) == true)
            .ToFrozenSet();

    private static IEnumerable<Type> Loadable(Assembly assembly)
    {
        try { return assembly.GetTypes(); }
        catch (ReflectionTypeLoadException error) { return error.Types.OfType<Type>(); }
    }
}
