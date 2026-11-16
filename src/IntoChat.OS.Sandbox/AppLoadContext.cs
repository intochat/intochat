using System.Reflection;
using System.Runtime.Loader;

namespace IntoChat.OS.Sandbox;

/// <summary>Collectible assembly load context that isolates sandboxed user apps and runtime synapses from host assemblies.</summary>
internal sealed class AppLoadContext(string name, string? contractsDirectory = null) : AssemblyLoadContext(name, isCollectible: true)
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

    private static string? _registeredContractsDirectory;
    private static int _resolverAttached;

    internal static void ConfigureContractsDirectory(string? contractsDirectory)
    {
        if (string.IsNullOrWhiteSpace(contractsDirectory))
        {
            return;
        }

        Volatile.Write(ref _registeredContractsDirectory, contractsDirectory);
        if (Interlocked.CompareExchange(ref _resolverAttached, 1, 0) == 0)
        {
            Default.Resolving += ResolveFromContractsDirectory;
        }
    }

    internal static bool IsAssemblyAllowed(string? assemblyName)
    {
        if (string.IsNullOrWhiteSpace(assemblyName))
        {
            return false;
        }

        if (IsForbidden(assemblyName))
        {
            return false;
        }

        if (string.Equals(assemblyName, "IntoChat.OS", StringComparison.OrdinalIgnoreCase)
            || string.Equals(assemblyName, "IntoChat.OS.UI", StringComparison.OrdinalIgnoreCase)
            || assemblyName.StartsWith("IntoChat.OS.Modules.", StringComparison.OrdinalIgnoreCase)
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

        foreach (var loaded in Default.Assemblies)
        {
            if (string.Equals(loaded.GetName().Name, assemblyName.Name, StringComparison.OrdinalIgnoreCase))
            {
                return loaded;
            }
        }

        try
        {
            return Default.LoadFromAssemblyName(assemblyName);
        }
        catch (FileNotFoundException)
        {
            if (TryProbePath(contractsDirectory ?? Volatile.Read(ref _registeredContractsDirectory), assemblyName.Name) is { } fromContracts)
            {
                return Default.LoadFromAssemblyPath(fromContracts);
            }

            if (TryProbePath(AppContext.BaseDirectory, assemblyName.Name) is { } fromBase)
            {
                return Default.LoadFromAssemblyPath(fromBase);
            }

            throw;
        }
    }

    private static bool IsForbidden(string assemblyName)
    {
        foreach (var forbidden in ForbiddenPrefixes)
        {
            if (string.Equals(assemblyName, forbidden, StringComparison.OrdinalIgnoreCase)
                || assemblyName.StartsWith(forbidden + ".", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static Assembly? ResolveFromContractsDirectory(AssemblyLoadContext context, AssemblyName assemblyName)
    {
        if (string.IsNullOrWhiteSpace(assemblyName.Name) || IsForbidden(assemblyName.Name))
        {
            return null;
        }

        var candidate = TryProbePath(Volatile.Read(ref _registeredContractsDirectory), assemblyName.Name);
        return candidate is null ? null : context.LoadFromAssemblyPath(candidate);
    }

    private static string? TryProbePath(string? directory, string? simpleName)
    {
        if (string.IsNullOrWhiteSpace(directory) || string.IsNullOrWhiteSpace(simpleName))
        {
            return null;
        }

        var candidate = Path.Combine(directory, simpleName + ".dll");
        return File.Exists(candidate) ? candidate : null;
    }
}
