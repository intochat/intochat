using System.Reflection;
using System.Reflection.Emit;
using DigitalBrain.Framework;
using DigitalBrain.Framework.Modules;
using DigitalBrain.Framework.Platform.Integrations;

namespace DigitalBrain.Architecture.Tests.Unit;

public sealed class CredentialBoundaryFacts
{
    private static readonly OpCode[] Codes = typeof(OpCodes).GetFields(BindingFlags.Public | BindingFlags.Static)
        .Where(f => f.FieldType == typeof(OpCode)).Select(f => (OpCode)f.GetValue(null)!).ToArray();

    // A with-expression copies an ambient stamp without the constructor, so a module may not set a
    // Platform Kind or StampedBy that way either. Downgrading a copy (ScriptEdge to App) stays legal.
    [Fact]
    public void NoModuleConstructsACallerContext()
    {
        var offenders = ModuleMethods().Where(m => Calls(m).Any(c => c.IsConstructor && c.DeclaringType == typeof(CallerContext)) || SetsAPlatformStamp(m))
            .Select(m => m.DeclaringType!.FullName + "." + m.Name).Order().ToArray();
        Assert.Empty(offenders);
    }

    // A platform stamp is any CallerContext whose Kind or StampedBy is set to Platform, by an object
    // initializer or by a with-expression. The OAuth callback stamps the owner recorded at login and
    // makes none. The authenticated edge constructs user stamps only, so it is the one other site
    // that may call the constructor.
    [Fact]
    public void OnlyTheIntegrationReleaseConstructsAPlatformStamp()
    {
        var platform = ProductionArchitecture.Assemblies.Single(a => a.GetName().Name == "DigitalBrain.Authority");
        var methods = platform.GetTypes().Where(t => t.Namespace?.StartsWith("OrleansCodeGen", StringComparison.Ordinal) != true)
            .SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly).Cast<MethodBase>()
                .Concat(t.GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static)))
            .ToArray();
        string[] Sites(Func<MethodBase, bool> does) => [.. methods.Where(does).Select(m => Outermost(m.DeclaringType!).FullName!).Distinct().Order()];

        Assert.Equal(["DigitalBrain.Authority.Integrations.IntegrationNeuron"],
            Sites(SetsAPlatformStamp));
        Assert.Equal(["DigitalBrain.Authority.Identity.AccountSession", "DigitalBrain.Authority.Integrations.IntegrationNeuron"],
            Sites(m => Calls(m).Any(c => c.IsConstructor && c.DeclaringType == typeof(CallerContext))));
    }

    private static Type Outermost(Type type) => type.DeclaringType is { } outer ? Outermost(outer) : type;

    private static bool SetsAPlatformStamp(MethodBase method)
    {
        int? constant = null;
        foreach (var (code, operand) in Instructions(method))
        {
            if ((code == OpCodes.Call || code == OpCodes.Callvirt) && Resolve(method, operand) is { } called
                && called.DeclaringType == typeof(CallerContext)
                && ((called.Name == "set_Kind" && constant == (int)CallerKind.Platform)
                    || (called.Name == "set_StampedBy" && constant == (int)TrustedEdge.Platform)))
            { return true; }
            constant = Constant(code, operand);
        }
        return false;
    }

    private static readonly OpCode[] SmallConstants = [OpCodes.Ldc_I4_0, OpCodes.Ldc_I4_1, OpCodes.Ldc_I4_2, OpCodes.Ldc_I4_3, OpCodes.Ldc_I4_4, OpCodes.Ldc_I4_5, OpCodes.Ldc_I4_6, OpCodes.Ldc_I4_7, OpCodes.Ldc_I4_8];

    private static int? Constant(OpCode code, int operand)
        => Array.IndexOf(SmallConstants, code) is var small and >= 0 ? small
            : code == OpCodes.Ldc_I4_S || code == OpCodes.Ldc_I4 ? operand
            : null;

    [Fact]
    public void OptionsAndContractsConfigurationCannotCarrySecrets()
    {
        var options = ProductionArchitecture.Types.Where(t => typeof(IModuleOptions).IsAssignableFrom(t) && !t.IsInterface);
        var configuration = ProductionArchitecture.Types.Where(t => t.Assembly.GetName().Name!.EndsWith(".Contracts", StringComparison.Ordinal)
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
            !m.Module.Assembly.GetName().Name!.EndsWith(".Aspire.Hosting", StringComparison.Ordinal)
            && !m.Module.Assembly.GetName().Name!.EndsWith(".Deployment", StringComparison.Ordinal)
            // Substrate providers must exist before the silo builds, before any integration grain exists.
            && m.DeclaringType?.FullName != "DigitalBrain.Microsoft.Orleans.OrleansStorageHosting")
            .Where(m => Calls(m).Any(c => c.DeclaringType == typeof(IIntegration)
                || (c.IsGenericMethod && c.GetGenericArguments().Contains(typeof(IIntegration)))
                || (c.DeclaringType == typeof(IntegrationSchema) && c.Name == nameof(IntegrationSchema.Key))))
            .Select(m => m.DeclaringType!.FullName + "." + m.Name).Order().ToArray();
        Assert.Empty(offenders);
    }

    private static IEnumerable<MethodBase> ModuleMethods() => ProductionArchitecture.Assemblies
        .Where(a => a.GetName().Name!.StartsWith("DigitalBrain.Modules.", StringComparison.Ordinal))
        .SelectMany(a => a.GetTypes()).Where(t => t.Namespace?.StartsWith("OrleansCodeGen", StringComparison.Ordinal) != true)
        .SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly)
            .Cast<MethodBase>().Concat(t.GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static)));

    private static IEnumerable<MethodBase> Calls(MethodBase method)
        => Instructions(method).Where(i => i.Code.OperandType == OperandType.InlineMethod)
            .Select(i => Resolve(method, i.Operand)).OfType<MethodBase>();

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
                OperandType.InlineI or OperandType.InlineMethod => BitConverter.ToInt32(bytes, position),
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
