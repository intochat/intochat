using System.Reflection;
using System.Security.Cryptography;
using System.Text;

namespace IntoChat.OS.Client.Runtime;

// What a brain and a sandbox host compare before a session may run behaviors: one "name=hash" line per
// contract assembly, the hash taken over its public surface (types and member signatures). Separate
// builds of the same source agree; a host built from other contracts does not. The host reports every
// assembly it carries; the brain requires each of its own lines to be present.
public static class ContractsFingerprint
{
    public static string Of(IEnumerable<Assembly> assemblies)
    {
        ArgumentNullException.ThrowIfNull(assemblies);
        return string.Join('\n', assemblies.Distinct().Select(Line).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal));
    }

    public static bool Covers(string? reported, string required)
    {
        ArgumentNullException.ThrowIfNull(required);
        var present = (reported ?? "").Split('\n', StringSplitOptions.RemoveEmptyEntries).ToHashSet(StringComparer.Ordinal);
        return required.Split('\n', StringSplitOptions.RemoveEmptyEntries).All(present.Contains);
    }

    private static string Line(Assembly assembly)
    {
        const BindingFlags Declared = BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;
        var surface = new StringBuilder();
        foreach (var type in (ContractsHost.Declares(assembly) ? ContractsHost.ContractTypes(assembly) : assembly.GetExportedTypes()).OrderBy(type => type.FullName, StringComparer.Ordinal))
        {
            surface.Append(type.FullName).Append(':').Append(type.BaseType?.FullName)
                .Append(':').AppendJoin(',', type.GetInterfaces().Select(contract => contract.FullName).Order(StringComparer.Ordinal)).Append('\n');
            foreach (var member in type.GetMembers(Declared).Select(member => member.MemberType + " " + member).Order(StringComparer.Ordinal))
            { surface.Append("  ").Append(member).Append('\n'); }
        }
        return assembly.GetName().Name + "=" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(surface.ToString())));
    }
}
