using System.Reflection;
using DigitalBrain.Access;

namespace IntoChat.OS.Architecture.Tests.Unit;

// Edges reach neurons only, so what must stay off them is simply not a neuron: the kernel and
// platform keep their state in plain grains. Every neuron method meets exactly one access policy.
public sealed class KernelBoundaryFacts
{
    // The connection's user face is the one platform contract a brain member calls.
    private static readonly string[] PlatformNeurons = ["IntoChat.OS.Connections.Platform.Connections.IConnection"];

    [Fact]
    public void NoKernelOrPlatformGrainInterfaceDerivesINeuron()
    {
        var offenders = ProductionArchitecture.Assemblies
            .Where(assembly => assembly.GetName().Name is "IntoChat.OS.Framework" or "IntoChat.OS.Authority")
            .SelectMany(assembly => assembly.GetTypes())
            .Where(type => type.IsInterface && typeof(INeuron).IsAssignableFrom(type) && type != typeof(INeuron)
                && !PlatformNeurons.Contains(type.FullName))
            .Select(type => type.FullName!).Order().ToArray();
        Assert.True(offenders.Length == 0, "Kernel or platform neuron contracts:\n" + string.Join("\n", offenders));
    }

    [Fact]
    public void EveryNeuronContractMethodResolvesToOneAccessPolicy()
    {
        var offenders = ProductionArchitecture.Types
            .Where(type => type.IsInterface && typeof(INeuron).IsAssignableFrom(type))
            .SelectMany(contract => contract.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                .Where(method => (method.GetCustomAttributes<AccessAttribute>().Any()
                    ? method.GetCustomAttributes<AccessAttribute>()
                    : contract.GetCustomAttributes<AccessAttribute>()).Count() > 1)
                .Select(method => contract.FullName + "." + method.Name))
            .Order().ToArray();
        Assert.True(offenders.Length == 0, "Methods with more than one access policy:\n" + string.Join("\n", offenders));
    }

    [Fact]
    public void EveryContractTheKernelCompilesForEdgesDerivesINeuron()
    {
        var types = new IntoChat.OS.Framework.CompiledTypes(ProductionArchitecture.Assemblies);

        Assert.All(types.Entries.Where(entry => entry.Kind == TypeKind.Neuron),
            // Transitional (06-03/06-07, deleted in 06-08): a migrated plain contract is a [DefaultNeuronType] interface instead.
            entry => Assert.True(typeof(INeuron).IsAssignableFrom(types.Contract(entry.Name)) || IntoChat.OS.Runtime.PlainNeuronContracts.Is(types.Contract(entry.Name)), entry.Name));
    }

    // The call filter applies access policy to INeuron contracts only, so an access attribute
    // anywhere else is dead and reads as a guarantee it is not.
    [Fact]
    public void AccessAttributesAppearOnlyOnNeuronContracts()
    {
        var offenders = ProductionArchitecture.Types
            .Where(type => type.IsInterface && !typeof(INeuron).IsAssignableFrom(type))
            .SelectMany(contract => contract.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                .Where(method => method.GetCustomAttributes<AccessAttribute>().Any())
                .Select(method => contract.FullName + "." + method.Name)
                .Concat(contract.GetCustomAttributes<AccessAttribute>().Any() ? [contract.FullName!] : []))
            .Order().ToArray();
        Assert.True(offenders.Length == 0, "Access attributes off neuron contracts:\n" + string.Join("\n", offenders));
    }
}
