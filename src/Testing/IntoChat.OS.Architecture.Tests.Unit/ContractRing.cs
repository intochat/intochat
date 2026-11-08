using System.Reflection;

namespace IntoChat.OS.Architecture.Tests.Unit;

// What counts as a contract assembly: a *.Contracts project, or a module assembly that declares it hosts its own
// contracts (IntoChatOSContractsHost) because no other module references them. A hosting assembly mixes contract
// types with the module's implementation, so the ring facts judge only the contract types in it.
internal static class ContractRing
{
    internal static bool IsContractsProject(Assembly assembly) => assembly.GetName().Name!.EndsWith(".Contracts", StringComparison.Ordinal);

    internal static bool IsHost(Assembly assembly) => ContractsHost.Declares(assembly);

    internal static bool IsContractAssembly(Assembly assembly) => IsContractsProject(assembly) || IsHost(assembly);

    // The contract types of an assembly: all of them in a Contracts project; in a host, only its contract surface.
    internal static Type[] ContractTypes(Assembly assembly)
        => IsContractsProject(assembly)
            ? [.. assembly.GetExportedTypes().Where(type => type.Namespace?.StartsWith("OrleansCodeGen", StringComparison.Ordinal) != true)]
            : ContractsHost.ContractTypes(assembly);
}
