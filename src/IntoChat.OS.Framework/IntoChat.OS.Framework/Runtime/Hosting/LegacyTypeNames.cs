using Orleans.Serialization;

namespace IntoChat.OS.Framework;

// Bytes stored before a type gained an alias name it by CLR full name and assembly ("Namespace.Type,Assembly").
// 0.7.0 moved namespaces without renaming assemblies (D-14): module vendor roots went under IntoChat.OS.Modules,
// and the Framework namespaces of Connections, Sdk and Contracts types follow their assemblies. Such a stored name
// is read as the type's current name in the same, already loaded assembly. Parsing only; writes keep the alias.
public sealed class LegacyTypeNames : ITypeConverter
{
    public bool TryFormat(Type type, out string formatted)
    {
        formatted = null!;
        return false;
    }

    public bool TryParse(string formatted, out Type type)
    {
        type = null!;
        if (formatted.Contains('[', StringComparison.Ordinal) || formatted.Split(',') is not [var name, var assembly]) { return false; }
        if (Current(name.Trim(), assembly.Trim()) is not { } current) { return false; }
        var loaded = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(candidate => !candidate.IsDynamic && candidate.GetName().Name == assembly.Trim());
        type = loaded?.GetType(current, throwOnError: false)!;
        return type is not null;
    }

    // The 0.7.0 name of a pre-0.7.0 type name, or null when the name did not move.
    internal static string? Current(string name, string assembly)
    {
        const string Root = "IntoChat.OS.", Framework = "IntoChat.OS.Framework.";
        if (assembly.StartsWith("IntoChat.OS.Modules.", StringComparison.Ordinal))
        {
            return name.StartsWith(Root, StringComparison.Ordinal) && !name.StartsWith("IntoChat.OS.Modules.", StringComparison.Ordinal)
                ? "IntoChat.OS.Modules." + name[Root.Length..] : null;
        }
        if (!name.StartsWith(Framework, StringComparison.Ordinal)) { return null; }
        var rest = name[Framework.Length..];
        return assembly switch
        {
            "IntoChat.OS.Connections" => "IntoChat.OS.Connections." + rest,
            "IntoChat.OS.Sdk" or "IntoChat.OS.Sdk.Identity" => "IntoChat.OS.Sdk." + rest,
            "IntoChat.OS.Contracts.Compute" => "IntoChat.OS.Contracts." + rest,
            "IntoChat.OS.Contracts.Configuration" => "IntoChat.OS.Contracts.Configuration." + (rest.StartsWith("Configuration.", StringComparison.Ordinal) ? rest["Configuration.".Length..] : rest),
            _ => null,
        };
    }
}
