using System.Reflection;
using System.Text;
using DigitalBrain.Access;
using DigitalBrain.Confirmation;
using DigitalBrain.Contracts;
using IntoChat.Sdk;

namespace IntoChat.OS.Host.Surface;

/// <summary>Marks an interface as a discoverable typed Neuron contract exposed to Plane 1 code orchestration.</summary>
[AttributeUsage(AttributeTargets.Interface, Inherited = false)]
public sealed class NeuronContractAttribute(string? alias = null) : Attribute
{
    /// <summary>Optional canonical alias (for example <c>"github"</c> or <c>"notifications"</c>).</summary>
    public string? Alias { get; } = alias;
}

/// <summary>
/// Marks a Neuron contract interface or method as requiring explicit Person confirmation
/// before automated Plane 1 orchestration code may invoke it.
/// </summary>
[AttributeUsage(AttributeTargets.Interface | AttributeTargets.Method, Inherited = false)]
public sealed class PersonOnlyAttribute(string? reason = null) : Attribute
{
    /// <summary>Optional human-readable explanation surfaced in the confirmation prompt.</summary>
    public string? Reason { get; } = reason;
}

/// <summary>One public method discovered on a typed Neuron contract.</summary>
public sealed record DiscoveredContractMethod(
    string Name,
    string Signature,
    bool RequiresPersonApproval,
    string? ApprovalReason = null);

/// <summary>Typed metadata for a discoverable Neuron contract callable from sandboxed orchestration scripts.</summary>
public sealed record DiscoveredNeuronContract(
    string Alias,
    string FullName,
    string ShortName,
    string? Namespace,
    string? DefaultNeuronType,
    string[] AllAliases,
    DiscoveredContractMethod[] Methods,
    bool IsSdkBuiltIn);

/// <summary>
/// Discovers typed Neuron contract interfaces (<see cref="NeuronContractAttribute"/>,
/// <see cref="PlainNeuronContracts"/>, <see cref="NeuronAliasAttribute"/>, and Sandbox SDK built-ins)
/// and enforces strict least-privilege scope matching for scoped <c>AppToken</c> grants.
/// </summary>
public static class NeuronContractDiscovery
{
    private static readonly string[] AuthorityContractPrefixes =
    [
        "DigitalBrain.Confirmation.",
        "DigitalBrain.Capabilities.Identity.",
        "DigitalBrain.Policies.",
        "IntoChat.OS.Modules.Apps.CodeBlocks.IClientGrants",
        "IntoChat.OS.Modules.Apps.Approvals.",
        "IntoChat.OS.Modules.Identity.",
    ];

    private static readonly (Type Type, string Alias, string[] ExtraAliases)[] SdkBuiltIns =
    [
        (typeof(INotifications), "notifications", ["notification", "notifications.inbox"]),
        (typeof(IFolder), "folder", ["folders"]),
        (typeof(IAi), "ai", ["sdk.ai"]),
        (typeof(ITableHandle), "tables", ["table"]),
    ];

    /// <summary>Returns true when the contract name belongs to an authority surface that sandboxed scripts may never invoke.</summary>
    public static bool IsAuthorityContract(string? contractName)
    {
        if (string.IsNullOrWhiteSpace(contractName))
        {
            return false;
        }

        var trimmed = contractName.Trim();
        return AuthorityContractPrefixes.Any(prefix =>
            trimmed.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            || string.Equals(trimmed, prefix.TrimEnd('.'), StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Returns true when <paramref name="type"/> is an authority contract or marked <see cref="InternalAttribute"/>.</summary>
    public static bool IsAuthorityOrInternal(Type type)
    {
        ArgumentNullException.ThrowIfNull(type);
        if (type.IsDefined(typeof(InternalAttribute), inherit: false))
        {
            return true;
        }

        return IsAuthorityContract(type.FullName) || IsAuthorityContract(type.Name);
    }

    /// <summary>Returns true when <paramref name="type"/> is a discoverable Neuron contract interface.</summary>
    public static bool IsDiscoverableContract(Type type)
    {
        ArgumentNullException.ThrowIfNull(type);
        if (!type.IsInterface || type.ContainsGenericParameters || !type.IsVisible)
        {
            return false;
        }

        if (IsAuthorityOrInternal(type))
        {
            return false;
        }

        if (IsSdkBuiltIn(type))
        {
            return true;
        }

        if (HasNeuronContractAttribute(type))
        {
            return true;
        }

        return PlainNeuronContracts.Is(type) || type.IsDefined(typeof(NeuronAliasAttribute), inherit: false);
    }

    /// <summary>
    /// Discovers typed Neuron contracts across the provided assemblies (or currently loaded non-dynamic assemblies),
    /// optionally restricted to contracts matching <paramref name="filterAliases"/>.
    /// </summary>
    public static DiscoveredNeuronContract[] Discover(
        IEnumerable<Assembly>? assemblies = null,
        IReadOnlyCollection<string>? filterAliases = null)
    {
        var sourceAssemblies = (assemblies ?? AppDomain.CurrentDomain.GetAssemblies())
            .Where(static assembly => !assembly.IsDynamic)
            .Distinct()
            .ToArray();

        var discovered = new List<DiscoveredNeuronContract>();
        var seenTypes = new HashSet<string>(StringComparer.Ordinal);

        foreach (var builtIn in SdkBuiltIns)
        {
            var entry = Describe(builtIn.Type);
            if (seenTypes.Add(entry.FullName))
            {
                discovered.Add(entry);
            }
        }

        foreach (var assembly in sourceAssemblies)
        {
            foreach (var type in LoadableTypes(assembly))
            {
                if (!IsDiscoverableContract(type))
                {
                    continue;
                }

                var fullName = type.FullName ?? type.Name;
                if (!seenTypes.Add(fullName))
                {
                    continue;
                }

                discovered.Add(Describe(type));
            }
        }

        if (filterAliases is { Count: > 0 })
        {
            var normalizedFilter = NormalizeScopes(filterAliases);
            discovered = discovered
                .Where(contract => MatchesAnyScope(contract.AllAliases, normalizedFilter))
                .ToList();
        }

        return discovered
            .OrderBy(static item => item.Alias, StringComparer.OrdinalIgnoreCase)
            .ThenBy(static item => item.FullName, StringComparer.Ordinal)
            .ToArray();
    }

    /// <summary>Builds typed contract metadata for a single interface type.</summary>
    public static DiscoveredNeuronContract Describe(Type contractType)
    {
        ArgumentNullException.ThrowIfNull(contractType);
        var fullName = contractType.FullName ?? contractType.Name;
        var alias = CanonicalAliasOf(contractType);
        var defaultNeuronType = contractType.GetCustomAttribute<DefaultNeuronTypeAttribute>(inherit: false)?.Type;
        var allAliases = AliasesOf(contractType);
        var isBuiltIn = IsSdkBuiltIn(contractType);
        var interfacePersonOnly = TryGetPersonOnlyReason(contractType, out var interfaceReason);

        var methods = contractType
            .GetMethods(BindingFlags.Instance | BindingFlags.Public)
            .Where(static method => !method.IsSpecialName)
            .OrderBy(static method => method.Name, StringComparer.Ordinal)
            .Select(method =>
            {
                var methodPersonOnly = interfacePersonOnly
                    || TryGetPersonOnlyReason(method, out var methodReason)
                    || UsesConfirmationTypes(method);
                string? approvalReason = null;
                if (methodPersonOnly)
                {
                    approvalReason = TryGetPersonOnlyReason(method, out var specific) && !string.IsNullOrWhiteSpace(specific)
                        ? specific
                        : !string.IsNullOrWhiteSpace(interfaceReason)
                            ? interfaceReason
                            : $"Calling {contractType.Name}.{method.Name} requires Person approval.";
                }

                return new DiscoveredContractMethod(
                    method.Name,
                    FormatMethodSignature(method),
                    methodPersonOnly,
                    approvalReason);
            })
            .ToArray();

        return new DiscoveredNeuronContract(
            Alias: alias,
            FullName: fullName,
            ShortName: contractType.Name,
            Namespace: contractType.Namespace,
            DefaultNeuronType: defaultNeuronType,
            AllAliases: allAliases,
            Methods: methods,
            IsSdkBuiltIn: isBuiltIn);
    }

    /// <summary>Returns the canonical alias for a contract type.</summary>
    public static string CanonicalAliasOf(Type contractType)
    {
        ArgumentNullException.ThrowIfNull(contractType);

        if (TryGetNeuronContractAlias(contractType) is { Length: > 0 } explicitAlias)
        {
            return explicitAlias.Trim().ToLowerInvariant();
        }

        foreach (var builtIn in SdkBuiltIns)
        {
            if (builtIn.Type == contractType || builtIn.Type.IsAssignableFrom(contractType))
            {
                return builtIn.Alias;
            }
        }

        if (contractType.GetCustomAttribute<NeuronAliasAttribute>(inherit: false)?.Alias is { Length: > 0 } neuronAlias)
        {
            return neuronAlias.Trim().ToLowerInvariant();
        }

        if (contractType.GetCustomAttribute<DefaultNeuronTypeAttribute>(inherit: false)?.Type is { Length: > 0 } defaultType)
        {
            return defaultType.Trim().ToLowerInvariant();
        }

        return DeriveAliasFromInterfaceName(contractType.Name);
    }

    /// <summary>Returns all normalized scope identifiers that match <paramref name="contractType"/>.</summary>
    public static string[] AliasesOf(Type contractType)
    {
        ArgumentNullException.ThrowIfNull(contractType);
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void AddIfPresent(string? value)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                set.Add(value.Trim());
            }
        }

        AddIfPresent(CanonicalAliasOf(contractType));
        AddIfPresent(TryGetNeuronContractAlias(contractType));
        AddIfPresent(contractType.FullName);
        AddIfPresent(contractType.Name);
        AddIfPresent(DeriveAliasFromInterfaceName(contractType.Name));

        if (contractType.GetCustomAttribute<NeuronAliasAttribute>(inherit: false) is { } aliasAttr)
        {
            AddIfPresent(aliasAttr.Alias);
            foreach (var former in aliasAttr.Formerly)
            {
                AddIfPresent(former);
            }
        }

        if (contractType.GetCustomAttribute<DefaultNeuronTypeAttribute>(inherit: false) is { } defaultTypeAttr)
        {
            AddIfPresent(defaultTypeAttr.Type);
        }

        foreach (var builtIn in SdkBuiltIns)
        {
            if (builtIn.Type == contractType || builtIn.Type.IsAssignableFrom(contractType))
            {
                AddIfPresent(builtIn.Alias);
                AddIfPresent(builtIn.Type.FullName);
                AddIfPresent(builtIn.Type.Name);
                foreach (var extra in builtIn.ExtraAliases)
                {
                    AddIfPresent(extra);
                }
            }
        }

        return [.. set.Order(StringComparer.OrdinalIgnoreCase)];
    }

    /// <summary>
    /// Enforces strict least-privilege scope matching between a requested contract identifier
    /// (alias or CLR full name) and a scoped AppToken's <paramref name="allowedScopes"/>.
    /// </summary>
    public static bool IsContractAllowed(
        string requestedContract,
        Type? resolvedContractType,
        IReadOnlyCollection<string> allowedScopes)
    {
        ArgumentNullException.ThrowIfNull(allowedScopes);
        if (string.IsNullOrWhiteSpace(requestedContract))
        {
            return false;
        }

        var trimmed = requestedContract.Trim();
        if (IsAuthorityContract(trimmed))
        {
            return false;
        }

        if (resolvedContractType is not null && IsAuthorityOrInternal(resolvedContractType))
        {
            return false;
        }

        if (allowedScopes.Count == 0)
        {
            return false;
        }

        var normalizedAllowed = NormalizeScopes(allowedScopes);
        if (normalizedAllowed.Count == 0)
        {
            return false;
        }

        var candidates = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { trimmed };
        var lastDot = trimmed.LastIndexOf('.');
        if (lastDot >= 0 && lastDot < trimmed.Length - 1)
        {
            var shortName = trimmed[(lastDot + 1)..];
            candidates.Add(shortName);
            candidates.Add(DeriveAliasFromInterfaceName(shortName));
        }

        foreach (var builtIn in SdkBuiltIns)
        {
            if (string.Equals(trimmed, builtIn.Type.FullName, StringComparison.OrdinalIgnoreCase)
                || string.Equals(trimmed, builtIn.Type.Name, StringComparison.OrdinalIgnoreCase)
                || string.Equals(trimmed, builtIn.Alias, StringComparison.OrdinalIgnoreCase)
                || builtIn.ExtraAliases.Contains(trimmed, StringComparer.OrdinalIgnoreCase))
            {
                candidates.Add(builtIn.Alias);
                candidates.Add(builtIn.Type.FullName!);
                candidates.Add(builtIn.Type.Name);
                foreach (var extra in builtIn.ExtraAliases)
                {
                    candidates.Add(extra);
                }
            }
        }

        if (resolvedContractType is not null)
        {
            foreach (var alias in AliasesOf(resolvedContractType))
            {
                candidates.Add(alias);
            }
        }

        return MatchesAnyScope(candidates, normalizedAllowed);
    }

    /// <summary>Normalizes and deduplicates a collection of contract scope aliases.</summary>
    public static string[] NormalizeScopeList(IEnumerable<string> scopes)
    {
        ArgumentNullException.ThrowIfNull(scopes);
        return scopes
            .Where(static s => !string.IsNullOrWhiteSpace(s))
            .Select(static s => s.Trim().ToLowerInvariant())
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
    }

    /// <summary>Renders a concise, prompt-ready typed C# catalog of discovered Neuron contracts for code generation.</summary>
    public static string FormatPromptCatalog(IReadOnlyList<DiscoveredNeuronContract> contracts)
    {
        ArgumentNullException.ThrowIfNull(contracts);
        if (contracts.Count == 0)
        {
            return "// No scoped Neuron contracts available.";
        }

        var sb = new StringBuilder();
        foreach (var contract in contracts)
        {
            sb.Append("// Contract alias: \"").Append(contract.Alias).Append('"');
            if (!string.IsNullOrWhiteSpace(contract.DefaultNeuronType))
            {
                sb.Append(" | NeuronType: \"").Append(contract.DefaultNeuronType).Append('"');
            }

            sb.AppendLine();
            sb.Append("// Acquire via: os.Get<").Append(contract.FullName).AppendLine(">(key)");
            sb.Append("interface ").Append(contract.FullName).AppendLine();
            sb.AppendLine("{");
            foreach (var method in contract.Methods)
            {
                if (method.RequiresPersonApproval)
                {
                    sb.AppendLine("    [PersonOnly]");
                }

                sb.Append("    ").Append(method.Signature).AppendLine(";");
            }

            sb.AppendLine("}");
            sb.AppendLine();
        }

        return sb.ToString();
    }

    private static HashSet<string> NormalizeScopes(IEnumerable<string> scopes)
        => new(
            scopes.Where(static s => !string.IsNullOrWhiteSpace(s)).Select(static s => s.Trim()),
            StringComparer.OrdinalIgnoreCase);

    private static bool MatchesAnyScope(IEnumerable<string> candidates, HashSet<string> normalizedAllowed)
        => candidates.Any(normalizedAllowed.Contains);

    private static bool IsSdkBuiltIn(Type type)
        => SdkBuiltIns.Any(entry => entry.Type == type);

    private static bool HasNeuronContractAttribute(Type type)
        => type.IsDefined(typeof(NeuronContractAttribute), inherit: false)
            || type.GetCustomAttributes(inherit: false)
                .Any(static attr => string.Equals(attr.GetType().Name, nameof(NeuronContractAttribute), StringComparison.Ordinal));

    private static string? TryGetNeuronContractAlias(Type type)
    {
        if (type.GetCustomAttribute<NeuronContractAttribute>(inherit: false) is { } direct)
        {
            return direct.Alias;
        }

        foreach (var attr in type.GetCustomAttributes(inherit: false))
        {
            if (string.Equals(attr.GetType().Name, nameof(NeuronContractAttribute), StringComparison.Ordinal))
            {
                return attr.GetType().GetProperty("Alias", BindingFlags.Instance | BindingFlags.Public)?.GetValue(attr) as string;
            }
        }

        return null;
    }

    private static bool TryGetPersonOnlyReason(MemberInfo member, out string? reason)
    {
        if (member.GetCustomAttribute<PersonOnlyAttribute>(inherit: false) is { } direct)
        {
            reason = direct.Reason;
            return true;
        }

        foreach (var attr in member.GetCustomAttributes(inherit: false))
        {
            var name = attr.GetType().Name;
            if (name is nameof(PersonOnlyAttribute) or "RequiresPersonAttribute" or "RequiresConfirmationAttribute")
            {
                reason = attr.GetType().GetProperty("Reason", BindingFlags.Instance | BindingFlags.Public)?.GetValue(attr) as string;
                return true;
            }
        }

        reason = null;
        return false;
    }

    private static bool UsesConfirmationTypes(MethodInfo method)
    {
        if (IsConfirmationType(method.ReturnType))
        {
            return true;
        }

        return method.GetParameters().Any(static p => IsConfirmationType(p.ParameterType));
    }

    private static bool IsConfirmationType(Type type)
    {
        if (type == typeof(ConfirmationPlan) || type == typeof(ConfirmationResult))
        {
            return true;
        }

        if (type.IsGenericType)
        {
            return type.GetGenericArguments().Any(IsConfirmationType);
        }

        return false;
    }

    private static string DeriveAliasFromInterfaceName(string name)
    {
        var stripped = name.Length > 1 && name[0] == 'I' && char.IsUpper(name[1])
            ? name[1..]
            : name;
        return stripped.ToLowerInvariant();
    }

    private static string FormatMethodSignature(MethodInfo method)
    {
        var returnType = FormatTypeName(method.ReturnType);
        var parameters = string.Join(", ", method.GetParameters().Select(static p => $"{FormatTypeName(p.ParameterType)} {p.Name}"));
        return $"{returnType} {method.Name}({parameters})";
    }

    private static string FormatTypeName(Type type)
    {
        if (type == typeof(void)) { return "void"; }
        if (type == typeof(string)) { return "string"; }
        if (type == typeof(int)) { return "int"; }
        if (type == typeof(long)) { return "long"; }
        if (type == typeof(bool)) { return "bool"; }
        if (type == typeof(double)) { return "double"; }
        if (type == typeof(object)) { return "object"; }
        if (type.IsArray) { return FormatTypeName(type.GetElementType()!) + "[]"; }
        if (type.IsGenericType)
        {
            var tick = type.Name.IndexOf('`', StringComparison.Ordinal);
            var baseName = tick > 0 ? type.Name[..tick] : type.Name;
            var args = string.Join(", ", type.GetGenericArguments().Select(FormatTypeName));
            return $"{baseName}<{args}>";
        }

        return type.Name;
    }

    private static IEnumerable<Type> LoadableTypes(Assembly assembly)
    {
        try
        {
            return assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException error)
        {
            return error.Types.OfType<Type>();
        }
    }
}
