using DigitalBrain.Contracts;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Text;

namespace IntoChat.OS.Modules.Microsoft.CSharp.Sandbox;

public static class ContractsHost
{
    public const string MetadataKey = "IntoChat.OS.ContractsHost";

    public static bool Declares(Assembly assembly)
    {
        ArgumentNullException.ThrowIfNull(assembly);
        return assembly.GetCustomAttributes<AssemblyMetadataAttribute>().Any(entry => entry.Key == MetadataKey && entry.Value == "true");
    }

    public static bool Declares(string assemblyPath)
    {
        ArgumentException.ThrowIfNullOrEmpty(assemblyPath);
        try
        {
            using var image = new PEReader(File.OpenRead(assemblyPath));
            if (!image.HasMetadata) { return false; }
            var metadata = image.GetMetadataReader();
            foreach (var handle in metadata.GetAssemblyDefinition().GetCustomAttributes())
            {
                var attribute = metadata.GetCustomAttribute(handle);
                if (attribute.Constructor.Kind != HandleKind.MemberReference) { continue; }
                var parent = metadata.GetMemberReference((MemberReferenceHandle)attribute.Constructor).Parent;
                if (parent.Kind != HandleKind.TypeReference) { continue; }
                var type = metadata.GetTypeReference((TypeReferenceHandle)parent);
                if (metadata.GetString(type.Name) != nameof(AssemblyMetadataAttribute)) { continue; }
                var blob = metadata.GetBlobBytes(attribute.Value);
                if (Strings(blob) is [MetadataKey, "true"]) { return true; }
            }
        }
        catch (BadImageFormatException) { return false; }
        catch (IOException) { return false; }
        return false;
    }

    public static Type[] ContractTypes(Assembly assembly)
    {
        ArgumentNullException.ThrowIfNull(assembly);
        var exported = assembly.GetExportedTypes().Where(type => type.Namespace?.StartsWith("OrleansCodeGen", StringComparison.Ordinal) != true).ToHashSet();
        var seen = new HashSet<Type>(exported.Where(type => DigitalBrain.Contracts.PlainNeuronContracts.Is(type)
            || (typeof(DigitalBrain.Signal).IsAssignableFrom(type) && type != typeof(DigitalBrain.Signal) && type.IsDefined(typeof(DigitalBrain.SignalAttribute), inherit: false))));
        var queue = new Queue<Type>(seen);
        while (queue.TryDequeue(out var type))
        {
            foreach (var part in Members(type).SelectMany(Unwrap).Where(part => part.Assembly == assembly && exported.Contains(part) && seen.Add(part))) { queue.Enqueue(part); }
        }
        return [.. seen.OrderBy(type => type.FullName, StringComparer.Ordinal)];
    }

    private static IEnumerable<Type> Members(Type type)
    {
        const BindingFlags Declared = BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;
        return type.GetMethods(Declared).SelectMany(method => method.GetParameters().Select(parameter => parameter.ParameterType).Append(method.ReturnType))
            .Concat(type.GetProperties(Declared).Select(property => property.PropertyType))
            .Concat(type.GetConstructors().SelectMany(constructor => constructor.GetParameters()).Select(parameter => parameter.ParameterType));
    }

    private static IEnumerable<Type> Unwrap(Type type)
    {
        if (type.IsByRef || type.IsArray) { return Unwrap(type.GetElementType()!); }
        if (type.IsGenericParameter) { return []; }
        return type.IsGenericType ? type.GetGenericArguments().SelectMany(Unwrap).Prepend(type.GetGenericTypeDefinition()) : [type];
    }

    private static string[] Strings(byte[] blob)
    {
        var read = new List<string>();
        var at = 2;
        while (at < blob.Length && read.Count < 2)
        {
            var length = blob[at++];
            if (length == 0xFF) { read.Add(""); continue; }
            if (at + length > blob.Length) { break; }
            read.Add(Encoding.UTF8.GetString(blob, at, length));
            at += length;
        }
        return [.. read];
    }
}
