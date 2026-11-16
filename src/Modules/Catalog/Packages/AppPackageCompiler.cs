using System.Collections.Immutable;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using DigitalBrain.Runtime.Synapses;
using DigitalBrain.Units;
using IntoChat.Sdk;
using Microsoft.Extensions.DependencyInjection;

namespace IntoChat.OS.Modules.Apps;

/// <summary>Result of validating and compiling an <see cref="AppPackage"/> into a sandbox-ready PE assembly.</summary>
internal sealed record CompiledAppPackage(
    bool Succeeded,
    byte[] AssemblyBytes,
    AppPackage? EnrichedPackage,
    string[] Errors);

/// <summary>
/// Compiles idiomatic C# <see cref="BrainApp{TState, TSettings}"/> packages and runtime synapses
/// through <see cref="SynapseCompiler"/>, enforces static PE safety bans, and extracts attribute
/// metadata directly from compiled PE metadata.
/// </summary>
internal sealed class AppPackageCompiler
{
    private static readonly string[] BannedTypePrefixes =
    [
        "System.IO.File",
        "System.IO.Directory",
        "System.IO.FileInfo",
        "System.IO.DirectoryInfo",
        "System.IO.FileStream",
        "System.IO.StreamReader",
        "System.IO.StreamWriter",
        "System.IO.FileSystemWatcher",
        "System.Diagnostics.Process",
        "System.Net.Http.HttpClient",
        "System.Net.Sockets",
        "System.Runtime.Loader.",
        "System.Runtime.InteropServices.DllImportAttribute",
        "System.Runtime.InteropServices.LibraryImportAttribute",
    ];

    private const string GlobalUsingsPath = "AppGlobalUsings.g.cs";

    private const string GlobalUsingsSource = """
        global using System.Linq;
        global using IntoChat.OS.UI;
        global using IntoChat.Sdk;
        global using SlotAttribute = IntoChat.Sdk.SlotAttribute;
        """;

    private static readonly Lazy<SynapseCompiler> DefaultCompiler =
        new(() => new SynapseCompiler(ReferenceSet.FromDefaultContext()));

    private readonly SynapseCompiler _compiler;

    public AppPackageCompiler()
        : this(null)
    {
    }

    [ActivatorUtilitiesConstructor]
    public AppPackageCompiler(SynapseCompiler? compiler = null)
    {
        _compiler = compiler ?? DefaultCompiler.Value;
    }

    public CompiledAppPackage Compile(AppPackage package)
    {
        ArgumentNullException.ThrowIfNull(package);
        if (package.SourceFiles.Length == 0)
        {
            return new CompiledAppPackage(false, [], null, ["Package contains no C# source files."]);
        }

        var files = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var file in package.SourceFiles.OrderBy(file => file.Path, StringComparer.Ordinal))
        {
            files[file.Path] = file.Code;
        }

        var isSynapseUnit = files.Values.Any(static code =>
            code.Contains("[Synapse", StringComparison.Ordinal)
            || code.Contains("[DigitalBrain.Synapse", StringComparison.Ordinal));
        if (!isSynapseUnit && !files.ContainsKey(GlobalUsingsPath))
        {
            files[GlobalUsingsPath] = GlobalUsingsSource;
        }

        var build = _compiler.Compile(new SynapseSource(package.Key, files, null));
        if (!build.Success)
        {
            var errors = build.Diagnostics
                .Select(d => $"{d.File}({d.Line}): {d.Id}: {d.Message}")
                .ToArray();
            if (errors.Length == 0)
            {
                errors = ["Compilation failed."];
            }

            return new CompiledAppPackage(false, [], null, errors);
        }

        using var pe = new PEReader(ImmutableArray.Create(build.Assembly));
        var reader = pe.GetMetadataReader();

        var bannedErrors = ValidateBannedApis(reader);
        if (bannedErrors.Count > 0)
        {
            return new CompiledAppPackage(false, [], null, [.. bannedErrors]);
        }

        var extracted = ExtractNeeds(reader, package);
        if (extracted.Error is { } metaError)
        {
            return new CompiledAppPackage(false, [], null, [metaError]);
        }

        var digest = AppPackage.ComputeDigest(
            extracted.Package!.Key,
            extracted.Package.Version,
            extracted.Package.SourceFiles,
            extracted.Package.DefaultSettingsJson);

        var finalPackage = extracted.Package with { Digest = digest };
        return new CompiledAppPackage(true, build.Assembly, finalPackage, []);
    }

    private static List<string> ValidateBannedApis(MetadataReader reader)
    {
        var errors = new HashSet<string>(StringComparer.Ordinal);

        foreach (var methodHandle in reader.MethodDefinitions)
        {
            var method = reader.GetMethodDefinition(methodHandle);
            if ((method.Attributes & MethodAttributes.PinvokeImpl) != 0 || !method.GetImport().Name.IsNil)
            {
                errors.Add($"P/Invoke '{reader.GetString(method.Name)}' is restricted in sandboxed apps.");
            }
        }

        foreach (var typeHandle in reader.TypeReferences)
        {
            var fullName = FullTypeName(reader, reader.GetTypeReference(typeHandle));
            CheckBannedType(fullName, errors);
        }

        foreach (var memberHandle in reader.MemberReferences)
        {
            var member = reader.GetMemberReference(memberHandle);
            var fullType = ResolveTypeName(reader, member.Parent);
            if (string.IsNullOrWhiteSpace(fullType))
            {
                continue;
            }

            CheckBannedType(fullType, errors);

            var memberName = reader.GetString(member.Name);
            if (fullType == "System.Environment" && memberName is "GetEnvironmentVariable" or "GetEnvironmentVariables" or "Exit")
            {
                errors.Add($"'System.Environment.{memberName}' is restricted in sandboxed apps.");
            }
            else if (fullType == "System.Reflection.Assembly" && memberName.StartsWith("Load", StringComparison.Ordinal))
            {
                errors.Add($"'System.Reflection.Assembly.{memberName}' is restricted in sandboxed apps.");
            }
        }

        return [.. errors];
    }

    private static void CheckBannedType(string fullTypeName, HashSet<string> errors)
    {
        if (BannedTypePrefixes.Any(prefix => fullTypeName.StartsWith(prefix, StringComparison.Ordinal)))
        {
            errors.Add($"'{fullTypeName}' is restricted in sandboxed apps. Use IntoChat.Sdk capabilities (Brain.Folder / Brain.Notifications / Brain.Ai / Brain.Tables) instead.");
        }
    }

    private static (AppPackage? Package, string? Error) ExtractNeeds(MetadataReader reader, AppPackage original)
    {
        var provider = MetadataTypeProvider.Instance;
        var appCandidates = new List<(TypeDefinition Type, CustomAttributeValue<string> Attr)>();

        foreach (var typeHandle in reader.TypeDefinitions)
        {
            var typeDef = reader.GetTypeDefinition(typeHandle);
            if ((typeDef.Attributes & (TypeAttributes.Interface | TypeAttributes.Abstract)) != 0)
            {
                continue;
            }

            foreach (var attrHandle in typeDef.GetCustomAttributes())
            {
                var attr = reader.GetCustomAttribute(attrHandle);
                if (AttributeTypeName(reader, attr) == typeof(AppAttribute).FullName)
                {
                    appCandidates.Add((typeDef, attr.DecodeValue(provider)));
                    break;
                }
            }
        }

        if (appCandidates.Count == 1)
        {
            return ExtractBrainAppNeeds(reader, provider, appCandidates[0].Type, appCandidates[0].Attr, original);
        }

        if (appCandidates.Count > 1)
        {
            return (null, "App package must declare a single [App] entry class.");
        }

        var synapseNeeds = TryExtractSynapseNeeds(reader, provider);
        if (synapseNeeds is not null)
        {
            return (original with { Needs = synapseNeeds }, null);
        }

        return (null, "App package must declare exactly one concrete class annotated with [IntoChat.Sdk.App(...)].");
    }

    private static (AppPackage? Package, string? Error) ExtractBrainAppNeeds(
        MetadataReader reader,
        MetadataTypeProvider provider,
        TypeDefinition appType,
        CustomAttributeValue<string> appAttr,
        AppPackage original)
    {
        if (!InheritsBrainApp(reader, provider, appType))
        {
            return (null, $"[App] class '{reader.GetString(appType.Name)}' must inherit from IntoChat.Sdk.BrainApp<TState, TSettings>.");
        }

        var attrKey = FixedString(appAttr, 0);
        var key = string.IsNullOrWhiteSpace(attrKey) ? original.Key : attrKey!;
        var title = NamedArg<string>(appAttr, nameof(AppAttribute.Title)) ?? (string.IsNullOrWhiteSpace(original.Title) ? key : original.Title);
        var summary = NamedArg<string>(appAttr, nameof(AppAttribute.Summary)) ?? original.Summary;
        var version = NamedArg<string>(appAttr, nameof(AppAttribute.Version)) ?? original.Version;
        var icon = NamedArg<string>(appAttr, nameof(AppAttribute.Icon)) ?? original.Icon;
        var category = NamedArg<string>(appAttr, nameof(AppAttribute.Category)) ?? original.Category;
        var author = NamedArg<string>(appAttr, nameof(AppAttribute.Author)) ?? original.Author;

        var slots = new List<AppSlotSpec>();
        foreach (var attrHandle in appType.GetCustomAttributes())
        {
            var attr = reader.GetCustomAttribute(attrHandle);
            if (AttributeTypeName(reader, attr) != typeof(SlotAttribute).FullName)
            {
                continue;
            }

            var decoded = attr.DecodeValue(provider);
            var slotName = FixedString(decoded, 0);
            var contractType = FixedString(decoded, 1);
            if (string.IsNullOrWhiteSpace(slotName) || string.IsNullOrWhiteSpace(contractType))
            {
                continue;
            }

            var required = NamedArgNullable<bool>(decoded, nameof(SlotAttribute.Required)) ?? true;
            var description = NamedArg<string>(decoded, nameof(SlotAttribute.Description)) ?? string.Empty;
            var accessRaw = NamedArgNullable<int>(decoded, nameof(SlotAttribute.Access)) ?? 0;
            var defaultPath = NamedArg<string>(decoded, nameof(SlotAttribute.DefaultPath)) ?? string.Empty;

            slots.Add(new AppSlotSpec(
                slotName,
                NormalizeContractName(contractType),
                required,
                description,
                (FolderAccess)accessRaw,
                defaultPath));
        }

        var signals = new SortedSet<string>(StringComparer.Ordinal);
        var schedules = new SortedSet<string>(StringComparer.Ordinal);
        var commands = new List<AppCommandSpec>();

        foreach (var methodHandle in appType.GetMethods())
        {
            var methodDef = reader.GetMethodDefinition(methodHandle);
            foreach (var attrHandle in methodDef.GetCustomAttributes())
            {
                var attr = reader.GetCustomAttribute(attrHandle);
                var attrName = AttributeTypeName(reader, attr);
                if (attrName == typeof(OnSignalAttribute).FullName)
                {
                    var sig = FixedString(attr.DecodeValue(provider), 0);
                    if (!string.IsNullOrWhiteSpace(sig))
                    {
                        signals.Add(sig);
                    }
                }
                else if (attrName == typeof(EveryAttribute).FullName)
                {
                    var cron = FixedString(attr.DecodeValue(provider), 0);
                    if (!string.IsNullOrWhiteSpace(cron))
                    {
                        schedules.Add(cron);
                    }
                }
                else if (attrName == typeof(OnCommandAttribute).FullName)
                {
                    var decoded = attr.DecodeValue(provider);
                    var cmd = FixedString(decoded, 0);
                    if (!string.IsNullOrWhiteSpace(cmd))
                    {
                        var desc = NamedArg<string>(decoded, nameof(OnCommandAttribute.Description)) ?? string.Empty;
                        commands.Add(new AppCommandSpec(cmd, desc));
                    }
                }
            }
        }

        var needs = new AppPackageNeeds([.. slots], [.. signals], [.. schedules], [.. commands]);
        return (original with
        {
            Key = key,
            Version = version,
            Title = title,
            Summary = summary,
            Icon = icon,
            Category = category,
            Author = author,
            Needs = needs,
        }, null);
    }

    private static AppPackageNeeds? TryExtractSynapseNeeds(MetadataReader reader, MetadataTypeProvider provider)
    {
        var hasState = false;
        var hasSynapse = false;
        var signalAliasesByType = new Dictionary<string, string>(StringComparer.Ordinal);
        var signals = new SortedSet<string>(StringComparer.Ordinal);
        var schedules = new SortedSet<string>(StringComparer.Ordinal);
        var slots = new List<AppSlotSpec>();

        foreach (var typeHandle in reader.TypeDefinitions)
        {
            var typeDef = reader.GetTypeDefinition(typeHandle);
            var fullName = FullTypeName(reader, typeDef);
            foreach (var attrHandle in typeDef.GetCustomAttributes())
            {
                var attr = reader.GetCustomAttribute(attrHandle);
                var attrName = AttributeTypeName(reader, attr);
                if (attrName == typeof(DigitalBrain.StateAttribute).FullName)
                {
                    hasState = true;
                }
                else if (attrName == typeof(DigitalBrain.SignalAttribute).FullName)
                {
                    var alias = FixedString(attr.DecodeValue(provider), 0);
                    if (!string.IsNullOrWhiteSpace(alias))
                    {
                        signalAliasesByType[fullName] = alias;
                    }
                }
            }
        }

        foreach (var typeHandle in reader.TypeDefinitions)
        {
            var typeDef = reader.GetTypeDefinition(typeHandle);
            foreach (var methodHandle in typeDef.GetMethods())
            {
                var methodDef = reader.GetMethodDefinition(methodHandle);
                foreach (var attrHandle in methodDef.GetCustomAttributes())
                {
                    var attr = reader.GetCustomAttribute(attrHandle);
                    if (AttributeTypeName(reader, attr) != typeof(DigitalBrain.SynapseAttribute).FullName)
                    {
                        continue;
                    }

                    hasSynapse = true;
                    var decoded = attr.DecodeValue(provider);
                    if (decoded.FixedArguments.Length > 1)
                    {
                        var arg0 = decoded.FixedArguments[0].Value as string;
                        if (!string.IsNullOrWhiteSpace(arg0))
                        {
                            var normalized = NormalizeContractName(arg0);
                            if (signalAliasesByType.TryGetValue(normalized, out var mapped))
                            {
                                signals.Add(mapped);
                            }
                            else if (arg0 == typeof(DigitalBrain.Time.Tick).FullName)
                            {
                                // Schedule signal represented below via Every named arg.
                            }
                            else
                            {
                                signals.Add(arg0);
                            }
                        }
                    }

                    var every = NamedArg<string>(decoded, "Every");
                    if (!string.IsNullOrWhiteSpace(every))
                    {
                        schedules.Add(every);
                    }
                }

                foreach (var paramHandle in methodDef.GetParameters())
                {
                    var param = reader.GetParameter(paramHandle);
                    var paramName = reader.GetString(param.Name);
                    foreach (var attrHandle in param.GetCustomAttributes())
                    {
                        var attr = reader.GetCustomAttribute(attrHandle);
                        if (AttributeTypeName(reader, attr) != typeof(DigitalBrain.SlotAttribute).FullName)
                        {
                            continue;
                        }

                        var decoded = attr.DecodeValue(provider);
                        var slotName = NamedArg<string>(decoded, nameof(DigitalBrain.SlotAttribute.Name)) ?? paramName;
                        if (!string.IsNullOrWhiteSpace(slotName) && slots.All(s => !string.Equals(s.Name, slotName, StringComparison.Ordinal)))
                        {
                            slots.Add(new AppSlotSpec(slotName, string.Empty, true, string.Empty, FolderAccess.Read, string.Empty));
                        }
                    }
                }
            }
        }

        if (!hasState || !hasSynapse)
        {
            return null;
        }

        return new AppPackageNeeds([.. slots], [.. signals], [.. schedules], []);
    }

    private static bool InheritsBrainApp(MetadataReader reader, MetadataTypeProvider provider, TypeDefinition typeDef)
    {
        var baseHandle = typeDef.BaseType;
        while (!baseHandle.IsNil)
        {
            var baseName = ResolveTypeName(reader, baseHandle, provider);
            if (baseName == "IntoChat.Sdk.BrainApp`2")
            {
                return true;
            }

            if (baseHandle.Kind == HandleKind.TypeDefinition)
            {
                baseHandle = reader.GetTypeDefinition((TypeDefinitionHandle)baseHandle).BaseType;
            }
            else
            {
                break;
            }
        }

        return false;
    }

    private static string ResolveTypeName(MetadataReader reader, EntityHandle handle, MetadataTypeProvider? provider = null)
        => handle.Kind switch
        {
            HandleKind.TypeReference => FullTypeName(reader, reader.GetTypeReference((TypeReferenceHandle)handle)),
            HandleKind.TypeDefinition => FullTypeName(reader, reader.GetTypeDefinition((TypeDefinitionHandle)handle)),
            HandleKind.TypeSpecification when provider is not null =>
                reader.GetTypeSpecification((TypeSpecificationHandle)handle).DecodeSignature(provider, genericContext: null),
            _ => string.Empty,
        };

    private static string FullTypeName(MetadataReader reader, TypeReference typeRef)
    {
        var ns = reader.GetString(typeRef.Namespace);
        var name = reader.GetString(typeRef.Name);
        return string.IsNullOrWhiteSpace(ns) ? name : $"{ns}.{name}";
    }

    private static string FullTypeName(MetadataReader reader, TypeDefinition typeDef)
    {
        var ns = reader.GetString(typeDef.Namespace);
        var name = reader.GetString(typeDef.Name);
        return string.IsNullOrWhiteSpace(ns) ? name : $"{ns}.{name}";
    }

    private static string? AttributeTypeName(MetadataReader reader, CustomAttribute attr)
    {
        return attr.Constructor.Kind switch
        {
            HandleKind.MemberReference => ResolveTypeName(reader, reader.GetMemberReference((MemberReferenceHandle)attr.Constructor).Parent),
            HandleKind.MethodDefinition => FullTypeName(
                reader,
                reader.GetTypeDefinition(reader.GetMethodDefinition((MethodDefinitionHandle)attr.Constructor).GetDeclaringType())),
            _ => null,
        };
    }

    private static string? FixedString(CustomAttributeValue<string> value, int index)
        => index < value.FixedArguments.Length ? value.FixedArguments[index].Value as string : null;

    private static T? NamedArg<T>(CustomAttributeValue<string> value, string name) where T : class
    {
        foreach (var pair in value.NamedArguments)
        {
            if (string.Equals(pair.Name, name, StringComparison.Ordinal) && pair.Value is T typed)
            {
                return typed;
            }
        }

        return null;
    }

    private static T? NamedArgNullable<T>(CustomAttributeValue<string> value, string name) where T : struct
    {
        foreach (var pair in value.NamedArguments)
        {
            if (string.Equals(pair.Name, name, StringComparison.Ordinal) && pair.Value is T typed)
            {
                return typed;
            }
        }

        return null;
    }

    private static string NormalizeContractName(string raw)
    {
        var comma = raw.IndexOf(',', StringComparison.Ordinal);
        return (comma >= 0 ? raw[..comma] : raw).Trim();
    }

    private sealed class MetadataTypeProvider : ICustomAttributeTypeProvider<string>, ISignatureTypeProvider<string, object?>
    {
        public static MetadataTypeProvider Instance { get; } = new();

        public string GetPrimitiveType(PrimitiveTypeCode typeCode) => typeCode.ToString();

        public string GetSystemType() => typeof(Type).FullName!;

        public string GetSZArrayType(string elementType) => elementType + "[]";

        public string GetArrayType(string elementType, ArrayShape shape) => elementType + "[]";

        public string GetByReferenceType(string elementType) => elementType + "&";

        public string GetPointerType(string elementType) => elementType + "*";

        public string GetPinnedType(string elementType) => elementType;

        public string GetTypeFromDefinition(MetadataReader mr, TypeDefinitionHandle handle, byte rawTypeKind)
            => FullTypeName(mr, mr.GetTypeDefinition(handle));

        public string GetTypeFromReference(MetadataReader mr, TypeReferenceHandle handle, byte rawTypeKind)
            => FullTypeName(mr, mr.GetTypeReference(handle));

        public string GetTypeFromSpecification(MetadataReader mr, object? genericContext, TypeSpecificationHandle handle, byte rawTypeKind)
            => mr.GetTypeSpecification(handle).DecodeSignature(this, genericContext);

        public string GetTypeFromSerializedName(string name) => NormalizeContractName(name);

        public PrimitiveTypeCode GetUnderlyingEnumType(string type) => PrimitiveTypeCode.Int32;

        public bool IsSystemType(string type) => string.Equals(type, typeof(Type).FullName, StringComparison.Ordinal);

        public string GetGenericInstantiation(string genericType, ImmutableArray<string> typeArguments) => genericType;

        public string GetGenericMethodParameter(object? genericContext, int index) => $"!!{index}";

        public string GetGenericTypeParameter(object? genericContext, int index) => $"!{index}";

        public string GetFunctionPointerType(MethodSignature<string> signature) => "fnptr";

        public string GetModifiedType(string modifier, string unmodifiedType, bool isRequired) => unmodifiedType;
    }
}
