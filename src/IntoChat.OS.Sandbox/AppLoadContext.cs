using System.Reflection;
using System.Runtime.Loader;

namespace IntoChat.OS.Sandbox;

/// <summary>Collectible assembly load context that isolates sandboxed user apps from host and runtime assemblies.</summary>
internal sealed class AppLoadContext(string name) : AssemblyLoadContext(name, isCollectible: true)
{
    private static readonly string[] ForbiddenPrefixes =
    [
        "System.Diagnostics.Process",
        "System.Net.Http",
        "System.Net.Sockets",
        "System.Net.WebSockets",
        "Microsoft.AspNetCore",
        "IntoChat.OS.Kernel",
        "IntoChat.OS.Framework",
        "IntoChat.OS.AppHost",
        "IntoChat.OS.Sandbox",
        "DigitalBrain.Runtime.Orleans",
        "DigitalBrain.Identity",
    ];

    internal static bool IsAssemblyAllowed(string? assemblyName)
    {
        if (string.IsNullOrWhiteSpace(assemblyName))
        {
            return false;
        }

        foreach (var forbidden in ForbiddenPrefixes)
        {
            if (string.Equals(assemblyName, forbidden, StringComparison.OrdinalIgnoreCase)
                || assemblyName.StartsWith(forbidden + ".", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        if (string.Equals(assemblyName, "IntoChat.OS", StringComparison.OrdinalIgnoreCase)
            || string.Equals(assemblyName, "Microsoft.Extensions.Logging.Abstractions", StringComparison.OrdinalIgnoreCase)
            || string.Equals(assemblyName, "netstandard", StringComparison.OrdinalIgnoreCase)
            || string.Equals(assemblyName, "mscorlib", StringComparison.OrdinalIgnoreCase)
            || string.Equals(assemblyName, "System.Private.CoreLib", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (string.Equals(assemblyName, "DigitalBrain", StringComparison.OrdinalIgnoreCase)
            || assemblyName.StartsWith("DigitalBrain.", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (string.Equals(assemblyName, "System", StringComparison.OrdinalIgnoreCase)
            || assemblyName.StartsWith("System.", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return false;
    }

    protected override Assembly? Load(AssemblyName assemblyName)
    {
        ArgumentNullException.ThrowIfNull(assemblyName);

        if (!IsAssemblyAllowed(assemblyName.Name))
        {
            throw new FileLoadException($"Assembly '{assemblyName.Name}' is not permitted in the app sandbox.");
        }

        return Default.LoadFromAssemblyName(assemblyName);
    }
}
