using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;

namespace DigitalBrain.Platform.Tests.Unit.Trust;

// The types of an assembly whose method bodies call GetGrain<T> for one of the given grain interfaces (including
// compiler-generated state machines and lambdas, reported under their declaring top-level type).
internal static class TrustGrainCallers
{
    public static string[] Find(Assembly assembly, IReadOnlyCollection<Type> grains)
    {
        var names = grains.Select(grain => grain.FullName!).ToHashSet(StringComparer.Ordinal);
        using var stream = File.OpenRead(assembly.Location);
        using var pe = new PEReader(stream);
        var reader = pe.GetMetadataReader();
        var targets = new HashSet<int>();
        for (var row = 1; row <= reader.GetTableRowCount(TableIndex.MethodSpec); row++)
        {
            var handle = MetadataTokens.MethodSpecificationHandle(row);
            var spec = reader.GetMethodSpecification(handle);
            if (MethodName(reader, spec.Method) != "GetGrain") { continue; }
            var arguments = spec.DecodeSignature(new NameProvider(), null);
            if (arguments.Any(names.Contains)) { targets.Add(MetadataTokens.GetToken(handle)); }
        }
        var callers = new HashSet<string>(StringComparer.Ordinal);
        foreach (var typeHandle in reader.TypeDefinitions)
        {
            var type = reader.GetTypeDefinition(typeHandle);
            foreach (var methodHandle in type.GetMethods())
            {
                var method = reader.GetMethodDefinition(methodHandle);
                if (method.RelativeVirtualAddress == 0) { continue; }
                var il = pe.GetMethodBody(method.RelativeVirtualAddress).GetILBytes()!;
                for (var i = 0; i + 4 < il.Length; i++)
                {
                    if (il[i] is not (0x28 or 0x6F)) { continue; }
                    var token = BitConverter.ToInt32(il, i + 1);
                    if (targets.Contains(token)) { callers.Add(TopLevel(reader, typeHandle)); }
                }
            }
        }
        return [.. callers];
    }

    private static string TopLevel(MetadataReader reader, TypeDefinitionHandle handle)
    {
        var type = reader.GetTypeDefinition(handle);
        while (type.GetDeclaringType() is { IsNil: false } outer) { handle = outer; type = reader.GetTypeDefinition(handle); }
        var ns = reader.GetString(type.Namespace);
        return ns.Length == 0 ? reader.GetString(type.Name) : $"{ns}.{reader.GetString(type.Name)}";
    }

    private static string MethodName(MetadataReader reader, EntityHandle method) => method.Kind switch
    {
        HandleKind.MemberReference => reader.GetString(reader.GetMemberReference((MemberReferenceHandle)method).Name),
        HandleKind.MethodDefinition => reader.GetString(reader.GetMethodDefinition((MethodDefinitionHandle)method).Name),
        _ => "",
    };

    private sealed class NameProvider : ISignatureTypeProvider<string, object?>
    {
        public string GetTypeFromDefinition(MetadataReader r, TypeDefinitionHandle handle, byte rawTypeKind)
        {
            var type = r.GetTypeDefinition(handle);
            return $"{r.GetString(type.Namespace)}.{r.GetString(type.Name)}";
        }
        public string GetTypeFromReference(MetadataReader r, TypeReferenceHandle handle, byte rawTypeKind)
        {
            var type = r.GetTypeReference(handle);
            return $"{r.GetString(type.Namespace)}.{r.GetString(type.Name)}";
        }
        public string GetTypeFromSpecification(MetadataReader r, object? context, TypeSpecificationHandle handle, byte rawTypeKind) => "";
        public string GetSZArrayType(string elementType) => elementType + "[]";
        public string GetArrayType(string elementType, ArrayShape shape) => elementType + "[*]";
        public string GetByReferenceType(string elementType) => elementType + "&";
        public string GetPointerType(string elementType) => elementType + "*";
        public string GetPinnedType(string elementType) => elementType;
        public string GetGenericInstantiation(string genericType, System.Collections.Immutable.ImmutableArray<string> typeArguments) => genericType;
        public string GetGenericMethodParameter(object? context, int index) => "";
        public string GetGenericTypeParameter(object? context, int index) => "";
        public string GetFunctionPointerType(MethodSignature<string> signature) => "";
        public string GetModifiedType(string modifier, string unmodifiedType, bool isRequired) => unmodifiedType;
        public string GetPrimitiveType(PrimitiveTypeCode typeCode) => typeCode.ToString();
    }
}
