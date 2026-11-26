using System.Reflection;
using System.Reflection.Emit;
using DigitalBrain.Capabilities;
using DigitalBrain.Capabilities.Connections.Connectors;
using DigitalBrain.Modules.Options;
using IntoChat.OS.Host.Configuration;
using IntoChat.OS.Modules.Identity.Contracts.Entries;
using IntoChat.OS.Modules.Identity.Contracts.Integrations;

namespace IntoChat.OS.Architecture.Tests.Unit;

public sealed class CredentialBoundaryFacts
{
    private static readonly OpCode[] SingleByteOpCodes = BuildSingleByteOpCodes();
    private static readonly OpCode[] TwoByteOpCodes = BuildTwoByteOpCodes();
    private static readonly string[] SecretSuffixes =
        ["ApiKey", "Secret", "Password", "ConnectionString", "RunTokenKey", "ConsumerKey", "PrivateKeyPem", "AdminConnection"];

    private static OpCode[] BuildSingleByteOpCodes()
    {
        var table = new OpCode[256];
        foreach (var field in typeof(OpCodes).GetFields(BindingFlags.Public | BindingFlags.Static))
        {
            if (field.FieldType == typeof(OpCode) && field.GetValue(null) is OpCode op && (ushort)op.Value < 256)
            {
                table[(ushort)op.Value] = op;
            }
        }
        return table;
    }

    private static OpCode[] BuildTwoByteOpCodes()
    {
        var table = new OpCode[256];
        foreach (var field in typeof(OpCodes).GetFields(BindingFlags.Public | BindingFlags.Static))
        {
            if (field.FieldType == typeof(OpCode) && field.GetValue(null) is OpCode op && ((ushort)op.Value >> 8) == 0xfe)
            {
                table[(ushort)op.Value & 0xff] = op;
            }
        }
        return table;
    }

    // Caller construction and kernel entry are covered by HouseRule04 (EnterKernel only in Kernel.cs and PlatformCall)
    // and OneCallerOsFacts (no CapabilityCaller, no db.cap.); Caller has no public constructor.
    [Fact]
    public void OptionsAndContractsConfigurationCannotCarrySecrets()
    {
        var options = ProductionArchitecture.Types.Where(t => typeof(IModuleOptions).IsAssignableFrom(t) && !t.IsInterface);
        var configuration = ProductionArchitecture.Types.Where(t =>
            (t.Name.EndsWith("Options", StringComparison.Ordinal) || t.Name.EndsWith("Settings", StringComparison.Ordinal))
            && ContractRing.IsContractType(t));
        var offenders = options.Concat(configuration).Distinct().SelectMany(t => t.GetProperties()
            .Where(p => p.PropertyType == typeof(string) && SecretName(p.Name)).Select(p => t.FullName + "." + p.Name)).ToArray();
        Assert.Empty(offenders);
    }

    private static bool SecretName(string name) => SecretSuffixes
        .Any(suffix => name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase));

    [Fact]
    public void RuntimeModulesCannotBypassIntegrationHandles()
    {
        var offenders = ModuleMethods().Where(m =>
            m.DeclaringType!.Assembly.GetName().Name != "IntoChat.OS.Modules.Identity" && m.DeclaringType!.Namespace != "IntoChat.OS.Kernel.Install"
            && !m.Module.Assembly.GetName().Name!.EndsWith(".Aspire.Hosting", StringComparison.Ordinal)
            && !m.Module.Assembly.GetName().Name!.EndsWith(".Deployment", StringComparison.Ordinal))
            .Where(m => Calls(m).Any(c => c.DeclaringType == typeof(IIntegration)
                || (c.IsGenericMethod && c.GetGenericArguments().Contains(typeof(IIntegration)))
                || (c.DeclaringType == typeof(IntegrationSchema) && c.Name == nameof(IntegrationSchema.Key))))
            .Select(m => m.DeclaringType!.FullName + "." + m.Name).Order().ToArray();
        Assert.Empty(offenders);
    }

    // IBrainProviderKeys releases a brain's plaintext provider key to whoever injects it, so only the
    // AI and Web modules (its consumers) and the identity module (its implementation) may reference it.
    [Fact]
    public void BrainProviderKeysLivesInTheIdentityModule()
        => Assert.Equal("IntoChat.OS.Modules.Identity.Contracts", typeof(IBrainProviderKeys).Assembly.GetName().Name);

    [Fact]
    public void OnlyTheAiModuleAndTheIdentityModuleTouchBrainProviderKeys()
    {
        var keys = typeof(IBrainProviderKeys);
        HashSet<string> allowed = new(StringComparer.Ordinal)
        {
            "IntoChat.OS.Modules.Identity",
            "IntoChat.OS.Modules.Identity.Contracts",
            "IntoChat.OS.Modules.AI",
        };
        var offenders = new HashSet<string>(StringComparer.Ordinal);
        foreach (var assembly in ProductionArchitecture.Assemblies)
        {
            var assemblyName = assembly.GetName().Name!;
            if (allowed.Contains(assemblyName)) { continue; }
            foreach (var t in assembly.GetTypes())
            {
                if (t.Namespace?.StartsWith("OrleansCodeGen", StringComparison.Ordinal) == true) { continue; }
                if (assemblyName == "IntoChat.OS.Modules.Plugins"
                    && (t.Namespace == "IntoChat.OS.Modules.Web" || t.Namespace?.StartsWith("IntoChat.OS.Modules.Web.", StringComparison.Ordinal) == true))
                {
                    continue;
                }
                var typeHasKeyField = t.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static)
                    .Any(f => f.FieldType == keys);
                foreach (var m in t.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly)
                    .Cast<MethodBase>()
                    .Concat(t.GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static)))
                {
                    if (typeHasKeyField
                        || m.GetParameters().Any(p => p.ParameterType == keys)
                        || (m as MethodInfo)?.ReturnType == keys
                        || ReferencesTypeInIl(m, keys))
                    {
                        offenders.Add(t.FullName + "." + m.Name);
                    }
                }
            }
        }
        Assert.Empty(offenders.Order().ToArray());
    }

    private static IEnumerable<MethodBase> ModuleMethods() => ProductionArchitecture.Assemblies
        .Where(a => a.GetName().Name!.StartsWith("IntoChat.OS.Modules.", StringComparison.Ordinal))
        .SelectMany(a => a.GetTypes()).Where(t => t.Namespace?.StartsWith("OrleansCodeGen", StringComparison.Ordinal) != true)
        .SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly)
            .Cast<MethodBase>().Concat(t.GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static)));

    private static IEnumerable<MethodBase> Calls(MethodBase method)
    {
        var bytes = method.GetMethodBody()?.GetILAsByteArray();
        if (bytes is null) { yield break; }
        Type[]? typeArgs = null;
        Type[]? methodArgs = null;
        for (var position = 0; position < bytes.Length;)
        {
            var code = NextOpCode(bytes, ref position);
            if (code.OperandType == OperandType.InlineMethod)
            {
                var token = BitConverter.ToInt32(bytes, position);
                typeArgs ??= method.DeclaringType?.GetGenericArguments();
                methodArgs ??= method.IsGenericMethod ? method.GetGenericArguments() : null;
                if (method.Module.ResolveMethod(token, typeArgs, methodArgs) is { } resolved)
                {
                    yield return resolved;
                }
            }
            position += OperandLength(code, bytes, position);
        }
    }

    private static bool ReferencesTypeInIl(MethodBase method, Type target)
    {
        var bytes = method.GetMethodBody()?.GetILAsByteArray();
        if (bytes is null) { return false; }
        Type[]? typeArgs = null;
        Type[]? methodArgs = null;
        for (var position = 0; position < bytes.Length;)
        {
            var code = NextOpCode(bytes, ref position);
            if (code.OperandType == OperandType.InlineMethod)
            {
                var token = BitConverter.ToInt32(bytes, position);
                typeArgs ??= method.DeclaringType?.GetGenericArguments();
                methodArgs ??= method.IsGenericMethod ? method.GetGenericArguments() : null;
                var resolved = method.Module.ResolveMethod(token, typeArgs, methodArgs);
                if (resolved is not null && (resolved.DeclaringType == target
                    || (resolved.IsGenericMethod && resolved.GetGenericArguments().Contains(target))))
                {
                    return true;
                }
            }
            else if (code.OperandType is OperandType.InlineTok or OperandType.InlineType)
            {
                var token = BitConverter.ToInt32(bytes, position);
                typeArgs ??= method.DeclaringType?.GetGenericArguments();
                methodArgs ??= method.IsGenericMethod ? method.GetGenericArguments() : null;
                if (ResolveType(method, token, typeArgs, methodArgs) == target)
                {
                    return true;
                }
            }
            position += OperandLength(code, bytes, position);
        }
        return false;
    }

    private static Type? ResolveType(MethodBase method, int token, Type[]? typeArgs, Type[]? methodArgs)
    {
        try { return method.Module.ResolveType(token, typeArgs, methodArgs); }
        catch (ArgumentException) { return null; }
    }

    private static OpCode NextOpCode(byte[] bytes, ref int position)
    {
        var first = bytes[position++];
        return first == 0xfe ? TwoByteOpCodes[bytes[position++]] : SingleByteOpCodes[first];
    }

    private static int OperandLength(OpCode code, byte[] bytes, int position) => code.OperandType switch
    {
        OperandType.InlineNone => 0,
        OperandType.ShortInlineBrTarget or OperandType.ShortInlineI or OperandType.ShortInlineVar => 1,
        OperandType.InlineVar => 2,
        OperandType.InlineI8 or OperandType.InlineR => 8,
        OperandType.InlineSwitch => 4 + 4 * BitConverter.ToInt32(bytes, position),
        _ => 4,
    };
}
