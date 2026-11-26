using System.Reflection;

namespace DigitalBrain.Runtime.Http.Surface;

/// <summary>
/// Supplies the exact set of assemblies a host exposes as neuron contract assemblies.
/// </summary>
public interface IContractAssemblySource
{
    /// <summary>Returns the exact contract assemblies exposed by the host.</summary>
    IReadOnlyList<Assembly> ContractAssemblies();
}
