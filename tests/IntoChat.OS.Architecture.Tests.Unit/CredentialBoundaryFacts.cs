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
    private static readonly OpCode[] Codes = typeof(OpCodes).GetFields(BindingFlags.Public | BindingFlags.Static)
        .Where(f => f.FieldType == typeof(OpCode)).Select(f => (OpCode)f.GetValue(null)!).ToArray();

    // Caller construction and kernel entry are covered by HouseRule04 (EnterKernel only in Kernel.cs and PlatformCall)
    // and OneCallerOsFacts (no CapabilityCaller, no db.cap.); Caller has no public constructor.
    [Fact]
    public void OptionsAndContractsConfigurationCannotCarrySecrets()
    {
        var options = ProductionArchitecture.Types.Where(t => typeof(IModuleOptions).IsAssignableFrom(t) && !t.IsInterface);
        var configuration = ProductionArchitecture.Types.Where(t => ContractRing.ContractTypes(t.Assembly).Contains(t)
            && (t.Name.EndsWith("Options", StringComparison.Ordinal) || t.Name.EndsWith("Settings", StringComparison.Ordinal)));
        var offenders = options.Concat(configuration).Distinct().SelectMany(t => t.GetProperties()
            .Where(p => p.PropertyType == typeof(string) && SecretName(p.Name)).Select(p => t.FullName + "." + p.Name)).ToArray();
        Assert.Empty(offenders);
    }

    private static bool SecretName(string name) => new[] { "ApiKey", "Secret", "Password", "ConnectionString", "RunTokenKey", "ConsumerKey", "PrivateKeyPem", "AdminConnection" }
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
        string[] allowed = ["IntoChat.OS.Modules.Identity", "IntoChat.OS.Modules.Identity.Contracts", "IntoChat.OS.Modules.AI", "IntoChat.OS.Modules.Web"];
        var offenders = ProductionArchitecture.Assemblies.Where(a => !allowed.Contains(a.GetName().Name))
            .SelectMany(a => a.GetTypes()).Where(t => t.Namespace?.StartsWith("OrleansCodeGen", StringComparison.Ordinal) != true)
            .SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly)
                .Cast<MethodBase>().Concat(t.GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static)))
            .Where(m => m.GetParameters().Any(p => p.ParameterType == keys)
                || (m as MethodInfo)?.ReturnType == keys
                || m.DeclaringType!.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static).Any(f => f.FieldType == keys)
                || Calls(m).Any(c => c.DeclaringType == keys || (c.IsGenericMethod && c.GetGenericArguments().Contains(keys)))
                || Tokens(m).Contains(keys))
            .Select(m => m.DeclaringType!.FullName + "." + m.Name).Distinct().Order().ToArray();
        Assert.Empty(offenders);
    }

    private static IEnumerable<MethodBase> ModuleMethods() => ProductionArchitecture.Assemblies
        .Where(a => a.GetName().Name!.StartsWith("IntoChat.OS.Modules.", StringComparison.Ordinal))
        .SelectMany(a => a.GetTypes()).Where(t => t.Namespace?.StartsWith("OrleansCodeGen", StringComparison.Ordinal) != true)
        .SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly)
            .Cast<MethodBase>().Concat(t.GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static)));

    private static IEnumerable<MethodBase> Calls(MethodBase method)
        => Instructions(method).Where(i => i.Code.OperandType == OperandType.InlineMethod)
            .Select(i => Resolve(method, i.Operand)).OfType<MethodBase>();

    // Types named by ldtoken (typeof) in a method body, e.g. GetService(typeof(IBrainProviderKeys)).
    private static IEnumerable<Type> Tokens(MethodBase method)
        => Instructions(method).Where(i => i.Code.OperandType is OperandType.InlineTok or OperandType.InlineType)
            .Select(i => ResolveType(method, i.Operand)).OfType<Type>();

    private static Type? ResolveType(MethodBase method, int token)
    {
        try { return method.Module.ResolveType(token, method.DeclaringType?.GetGenericArguments(), method.IsGenericMethod ? method.GetGenericArguments() : null); }
        catch (ArgumentException) { return null; }
    }

    private static MethodBase? Resolve(MethodBase method, int token)
        => method.Module.ResolveMethod(token, method.DeclaringType?.GetGenericArguments(), method.IsGenericMethod ? method.GetGenericArguments() : null);

    // Each opcode with its inline 1- or 4-byte operand (0 for any other operand shape).
    private static IEnumerable<(OpCode Code, int Operand)> Instructions(MethodBase method)
    {
        var bytes = method.GetMethodBody()?.GetILAsByteArray();
        if (bytes is null) { yield break; }
        for (var position = 0; position < bytes.Length;)
        {
            short value = bytes[position++];
            if (value == 0xfe) { value = unchecked((short)(0xfe00 | bytes[position++])); }
            var code = Codes.Single(c => c.Value == value);
            yield return (code, code.OperandType switch
            {
                OperandType.ShortInlineI => unchecked((sbyte)bytes[position]),
                OperandType.InlineI or OperandType.InlineMethod or OperandType.InlineTok or OperandType.InlineType => BitConverter.ToInt32(bytes, position),
                _ => 0,
            });
            position += code.OperandType switch
            {
                OperandType.InlineNone => 0,
                OperandType.ShortInlineBrTarget or OperandType.ShortInlineI or OperandType.ShortInlineVar => 1,
                OperandType.InlineVar => 2,
                OperandType.InlineI8 or OperandType.InlineR => 8,
                OperandType.InlineSwitch => 4 + 4 * BitConverter.ToInt32(bytes, position),
                _ => 4,
            };
        }
    }
}
