using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Text;

namespace IntoChat.OS.Client.Runtime;

// A module whose contracts no other module references keeps them in its own assembly. That assembly says so
// with assembly metadata (set by the IntoChatOSContractsHost project property), so the sandbox image, the script
// compiler and the architecture facts find contract assemblies by what they declare, never by their name.
public static class ContractsHost
{
    public const string MetadataKey = "IntoChat.OS.ContractsHost";

    public static bool Declares(Assembly assembly)
    {
        ArgumentNullException.ThrowIfNull(assembly);
        return assembly.GetCustomAttributes<AssemblyMetadataAttribute>().Any(entry => entry.Key == MetadataKey && entry.Value == "true");
    }

    // Reads the marker from the file alone, so nothing is loaded to ask.
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
        catch (BadImageFormatException) { }
        catch (IOException) { }
        return false;
    }

    // A custom attribute value is the 0x0001 prolog, then each string argument as a compressed length and UTF-8.
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
