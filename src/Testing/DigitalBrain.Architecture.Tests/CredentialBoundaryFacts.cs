using System.Reflection;
using System.Reflection.Emit;
using DigitalBrain;
using DigitalBrain.Framework.Enforcement;
using DigitalBrain.Framework.Platform.Integrations;
using DigitalBrain.Framework;

namespace DigitalBrain.Architecture.Tests;

public sealed class CredentialBoundaryFacts
{
    private static readonly OpCode[] Codes = typeof(OpCodes).GetFields(BindingFlags.Public | BindingFlags.Static)
        .Where(f => f.FieldType == typeof(OpCode)).Select(f => (OpCode)f.GetValue(null)!).ToArray();

    [Fact]
    public void NoModuleConstructsACallerContext()
    {
        var offenders = ModuleMethods().Where(m => Calls(m).Any(c => c.IsConstructor && c.DeclaringType == typeof(CallerContext)))
            .Select(m => m.DeclaringType!.FullName + "." + m.Name).Order().ToArray();
        Assert.Empty(offenders);
    }

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
            && !m.Module.Assembly.GetName().Name!.EndsWith(".Deployment", StringComparison.Ordinal))
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
    {
        var bytes = method.GetMethodBody()?.GetILAsByteArray();
        if (bytes is null) { yield break; }
        for (var position = 0; position < bytes.Length;)
        {
            short value = bytes[position++];
            if (value == 0xfe) { value = unchecked((short)(0xfe00 | bytes[position++])); }
            var code = Codes.Single(c => c.Value == value);
            if (code.OperandType == OperandType.InlineMethod)
            {
                var token = BitConverter.ToInt32(bytes, position);
                var called = method.Module.ResolveMethod(token, method.DeclaringType?.GetGenericArguments(), method.IsGenericMethod ? method.GetGenericArguments() : null);
                if (called is not null) { yield return called; }
            }
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
