using System.Collections.Concurrent;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Runtime.Loader;

namespace DigitalBrain.Runtime.Synapses;

/// <summary>A collectible context for one synapse assembly; framework assemblies resolve from the default context.</summary>
internal sealed class SynapseLoadContext(string name) : AssemblyLoadContext(name, isCollectible: true)
{
    private static readonly ConcurrentDictionary<string, byte[]> ForwardingShims = new(StringComparer.OrdinalIgnoreCase);

    protected override Assembly? Load(AssemblyName assemblyName)
    {
        if (Default.Assemblies.FirstOrDefault(loaded =>
                AssemblyName.ReferenceMatchesDefinition(assemblyName, loaded.GetName())
                || string.Equals(loaded.GetName().Name, assemblyName.Name, StringComparison.OrdinalIgnoreCase)) is { } shared)
        {
            return shared;
        }

        if (assemblyName.Name is { Length: > 0 } simple)
        {
            var candidate = Path.Combine(AppContext.BaseDirectory, simple + ".dll");
            if (File.Exists(candidate))
            {
                return Default.LoadFromAssemblyPath(candidate);
            }

            if (Default.Assemblies.FirstOrDefault(loaded =>
                    loaded.GetCustomAttributes<AssemblyMetadataAttribute>().Any(meta =>
                        string.Equals(meta.Key, "DigitalBrain.ReplacesAssembly", StringComparison.Ordinal)
                        && string.Equals(meta.Value, simple, StringComparison.OrdinalIgnoreCase))) is { } replacement)
            {
                var shimBytes = ForwardingShims.GetOrAdd(simple, static (alias, target) => BuildForwardingShim(alias, target), replacement);
                using var stream = new MemoryStream(shimBytes, writable: false);
                return LoadFromStream(stream);
            }
        }

        return null;
    }

    private static byte[] BuildForwardingShim(string aliasName, Assembly target)
    {
        var metadata = new MetadataBuilder();
        metadata.AddModule(
            0,
            metadata.GetOrAddString(aliasName + ".dll"),
            metadata.GetOrAddGuid(Guid.NewGuid()),
            default,
            default);
        metadata.AddAssembly(
            metadata.GetOrAddString(aliasName),
            new Version(1, 0, 0, 0),
            default,
            default,
            default,
            AssemblyHashAlgorithm.None);
        metadata.AddTypeDefinition(
            default,
            default,
            metadata.GetOrAddString("<Module>"),
            default,
            MetadataTokens.FieldDefinitionHandle(1),
            MetadataTokens.MethodDefinitionHandle(1));

        var targetName = target.GetName();
        var targetRef = metadata.AddAssemblyReference(
            metadata.GetOrAddString(targetName.Name!),
            targetName.Version ?? new Version(0, 0, 0, 0),
            metadata.GetOrAddString(targetName.CultureName ?? string.Empty),
            metadata.GetOrAddBlob(targetName.GetPublicKeyToken() ?? []),
            default,
            default);

        const TypeAttributes TypeForwarder = (TypeAttributes)0x00200000;
        foreach (var exported in target.GetExportedTypes())
        {
            if (!exported.IsNested)
            {
                metadata.AddExportedType(
                    TypeAttributes.Public | TypeForwarder,
                    metadata.GetOrAddString(exported.Namespace ?? string.Empty),
                    metadata.GetOrAddString(exported.Name),
                    targetRef,
                    0);
            }
        }

        var peBuilder = new ManagedPEBuilder(
            PEHeaderBuilder.CreateLibraryHeader(),
            new MetadataRootBuilder(metadata),
            new BlobBuilder());
        var blob = new BlobBuilder();
        peBuilder.Serialize(blob);
        return blob.ToArray();
    }
}
