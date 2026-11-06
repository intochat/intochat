using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using Microsoft.CodeAnalysis;

namespace IntoChat.OS.Modules.Microsoft.CSharp.Catalog;

/// <summary>
/// Process-wide shared cache of Roslyn <see cref="MetadataReference"/> sets.
/// Avoids repeated disk rescanning and assembly loading across C# script compilation runs.
/// </summary>
public static class SharedMetadataReferenceCache
{
    private static readonly Lazy<IReadOnlyList<MetadataReference>> StandardReferencesLazy = new(LoadStandardReferences);

    /// <summary>
    /// Gets the standard process-wide cached metadata references for C# script/app compilation.
    /// Includes runtime assemblies, core vocabulary, client contracts, and module contracts.
    /// </summary>
    public static IReadOnlyList<MetadataReference> StandardReferences => StandardReferencesLazy.Value;

    private static IReadOnlyList<MetadataReference> LoadStandardReferences()
    {
        var tpa = (string?)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES");
        if (string.IsNullOrEmpty(tpa))
        {
            // Fallback to core assemblies loaded in current domain
            return
            [
                MetadataReference.CreateFromFile(typeof(object).Assembly.Location),
                MetadataReference.CreateFromFile(typeof(Enumerable).Assembly.Location),
                MetadataReference.CreateFromFile(typeof(Task).Assembly.Location),
            ];
        }

        var available = tpa
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Where(File.Exists)
            .DistinctBy(Path.GetFileNameWithoutExtension, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(path => Path.GetFileNameWithoutExtension(path)!, StringComparer.OrdinalIgnoreCase);

        var runtime = Path.GetDirectoryName(typeof(object).Assembly.Location)!;
        var carried = new HashSet<string>(
            available.Where(entry => Path.GetDirectoryName(entry.Value) == runtime).Select(entry => entry.Key),
            StringComparer.OrdinalIgnoreCase);

        // A contract assembly is a *.Contracts project or a module assembly that declares it hosts its own contracts;
        // the core vocabulary packages DigitalBrain and DigitalBrain.UI are seeded with IntoChat.OS.
        var pending = new Stack<string>(available.Where(entry =>
            entry.Key is "IntoChat.OS" or "DigitalBrain.Client" or "DigitalBrain" or "DigitalBrain.UI"
            || entry.Key.EndsWith(".Contracts", StringComparison.OrdinalIgnoreCase)
            || (entry.Key.StartsWith("IntoChat.OS.Modules.", StringComparison.OrdinalIgnoreCase) && ContractsHost.Declares(entry.Value)))
            .Select(entry => entry.Key));

        while (pending.TryPop(out var name))
        {
            if (!carried.Add(name)) { continue; }

            // A hosting module assembly is carried for its contract surface only; its implementation's dependencies are not a script's to reach.
            if (name.StartsWith("IntoChat.OS.Modules.", StringComparison.OrdinalIgnoreCase) && !name.EndsWith(".Contracts", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            try
            {
                using var stream = File.OpenRead(available[name]);
                using var image = new PEReader(stream);
                var metadata = PEReaderExtensions.GetMetadataReader(image);
                foreach (var handle in metadata.AssemblyReferences)
                {
                    var dependency = metadata.GetString(metadata.GetAssemblyReference(handle).Name);
                    if (available.ContainsKey(dependency) && !carried.Contains(dependency))
                    {
                        pending.Push(dependency);
                    }
                }
            }
            catch (Exception ex) when (ex is BadImageFormatException or IOException or NotSupportedException)
            {
                // Non-managed or unreadable assembly
            }
        }

        return [.. carried.Select(name => (MetadataReference)MetadataReference.CreateFromFile(available[name]))];
    }
}
