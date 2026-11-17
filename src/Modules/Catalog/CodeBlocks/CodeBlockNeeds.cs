using System.Collections.Immutable;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;
using System.Text;

namespace IntoChat.OS.Modules.Apps.CodeBlocks;

// A code block's needs are read from its compiled form, not declared: every generic call on the brain client (Brain.Get<T> or
// Brain.Slot<T>) names one contract. What the person approves is exactly what the code can reach; the edge refuses anything else.
internal static class CodeBlockNeeds
{
    private static readonly string[] ClientMethods = ["Get", "Slot"];
    private const string ClientType = "IntoChat.Sdk.ISandboxBrain";

    internal static string[] Infer(byte[] assembly)
    {
        using var pe = new PEReader(ImmutableArray.Create(assembly));
        var reader = pe.GetMetadataReader();
        var needs = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var handle in Enumerable.Range(1, reader.GetTableRowCount(TableIndex.MethodSpec)).Select(MetadataTokens.MethodSpecificationHandle))
        {
            var spec = reader.GetMethodSpecification(handle);
            if (spec.Method.Kind != HandleKind.MemberReference) { continue; }
            var member = reader.GetMemberReference((MemberReferenceHandle)spec.Method);
            if (!ClientMethods.Contains(reader.GetString(member.Name), StringComparer.Ordinal) || Owner(reader, member.Parent) != ClientType) { continue; }
            foreach (var argument in spec.DecodeSignature(Names.Instance, null)) { needs.Add(argument); }
        }
        return [.. needs];
    }

    // Contracts no code block may need: the ones that decide authority. A block acts for the person who approved it, so reaching
    // these would let it answer its own approvals or widen its own reach.
    private static readonly string[] AuthorityContracts =
    [
        "DigitalBrain.Confirmation.", "DigitalBrain.Capabilities.Identity.", "IntoChat.OS.Modules.Apps.CodeBlocks.IClientGrants",
        "IntoChat.OS.Modules.Apps.Approvals.", "DigitalBrain.Policies.",
    ];

    internal static string[] Forbidden(string[] needs)
        => [.. needs.Where(need => AuthorityContracts.Any(prefix => need.StartsWith(prefix, StringComparison.Ordinal)))];

    internal static string Hash(string[] needs)
        => "needs:" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("\n", needs.Order(StringComparer.Ordinal)))))[..16];

    private static string? Owner(MetadataReader reader, EntityHandle parent) => parent.Kind switch
    {
        HandleKind.TypeReference => Name(reader, reader.GetTypeReference((TypeReferenceHandle)parent)),
        HandleKind.TypeDefinition => reader.GetString(reader.GetTypeDefinition((TypeDefinitionHandle)parent).Namespace) + "." + reader.GetString(reader.GetTypeDefinition((TypeDefinitionHandle)parent).Name),
        _ => null,
    };

    private static string Name(MetadataReader reader, TypeReference type)
        => reader.GetString(type.Namespace) is { Length: > 0 } ns ? ns + "." + reader.GetString(type.Name) : reader.GetString(type.Name);

    // Full type names of generic arguments; anything that is not a plain named type is not a contract and reads as its kind.
    private sealed class Names : ISignatureTypeProvider<string, object?>
    {
        internal static readonly Names Instance = new();

        public string GetTypeFromReference(MetadataReader reader, TypeReferenceHandle handle, byte rawTypeKind) => Name(reader, reader.GetTypeReference(handle));

        public string GetTypeFromDefinition(MetadataReader reader, TypeDefinitionHandle handle, byte rawTypeKind)
        {
            var type = reader.GetTypeDefinition(handle);
            return reader.GetString(type.Namespace) is { Length: > 0 } ns ? ns + "." + reader.GetString(type.Name) : reader.GetString(type.Name);
        }

        public string GetTypeFromSpecification(MetadataReader reader, object? context, TypeSpecificationHandle handle, byte rawTypeKind) => "(type specification)";
        public string GetPrimitiveType(PrimitiveTypeCode typeCode) => typeCode.ToString();
        public string GetGenericInstantiation(string genericType, ImmutableArray<string> typeArguments) => $"{genericType}<{string.Join(",", typeArguments)}>";
        public string GetArrayType(string elementType, ArrayShape shape) => elementType + "[]";
        public string GetSZArrayType(string elementType) => elementType + "[]";
        public string GetByReferenceType(string elementType) => elementType + "&";
        public string GetPointerType(string elementType) => elementType + "*";
        public string GetPinnedType(string elementType) => elementType;
        public string GetFunctionPointerType(MethodSignature<string> signature) => "(function pointer)";
        public string GetGenericMethodParameter(object? context, int index) => "(generic parameter)";
        public string GetGenericTypeParameter(object? context, int index) => "(generic parameter)";
        public string GetModifiedType(string modifier, string unmodifiedType, bool isRequired) => unmodifiedType;
    }
}
