using System.Reflection;
using DigitalBrain.Access;

namespace DigitalBrain.Architecture.Tests.Unit;

// One caller: DigitalBrain.Access.Caller is the only identity a call carries. The old models stay deleted, no rule reads a
// request-supplied identity, no neuron contract or port takes a caller as an argument, and the kernel scope is entered only by host code.
public sealed class OneCallerFacts
{
    private static readonly string[] OldCallerTypes =
        ["CapabilityCaller", "CapabilityCallers", "CallerKind", "TrustedEdge", "RuntimeCallerContext", "LegacyCallers", "AccessCounters"];

    // Files under framework/src that may call EdgeAuthority.EnterKernel, relative to framework/src.
    private static readonly string[] EnterKernelSites =
    [
        "DigitalBrain/Access/EdgeAuthority.cs",                               // the definition
        "DigitalBrain.Identity.Local/Vault/LocalVaultPort.cs",                // platform port: runs its own owner check, then reads the vault as platform code
        "DigitalBrain.Testing.Silo/Conformance/Host/HostConformanceFacts.cs", // test host harness acting as the platform
    ];

    [Fact]
    public void NoOldCallerTypeExists()
    {
        var found = FrameworkTypes().Where(type => OldCallerTypes.Contains(type.Name, StringComparer.Ordinal)).Select(type => type.FullName).ToList();
        Assert.True(found.Count == 0, string.Join(", ", found));
    }

    [Fact]
    public void NoRequestStringIdentity()
    {
        var offenders = FrameworkSources().Where(path => File.ReadAllText(path).Contains("\"db.cap.", StringComparison.Ordinal)).ToList();
        Assert.True(offenders.Count == 0, string.Join(", ", offenders));
    }

    [Fact]
    public void NoInterfaceMethodTakesACaller()
    {
        var offenders = FrameworkTypes()
            .Where(type => type.IsInterface && (type.IsPublic || type.IsNestedPublic))
            .SelectMany(type => type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly))
            .Where(method => method.GetParameters().Any(parameter => parameter.ParameterType == typeof(Caller)))
            .Select(method => $"{method.DeclaringType!.FullName}.{method.Name}")
            .ToList();
        Assert.True(offenders.Count == 0, string.Join(", ", offenders));
    }

    [Fact]
    public void EnterKernelOnlyAtAllowedSites()
    {
        var root = Path.Combine(Repository.Root, "src");
        var allowed = EnterKernelSites.Select(site => Path.GetFullPath(Path.Combine(root, site))).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var offenders = FrameworkSources()
            .Where(path => File.ReadAllText(path).Contains("EnterKernel(", StringComparison.Ordinal))
            .Where(path => !allowed.Contains(Path.GetFullPath(path)))
            .ToList();
        Assert.True(offenders.Count == 0, string.Join(", ", offenders));
        Assert.All(allowed, site => Assert.True(File.Exists(site), $"{site} is on the allowlist but missing."));
    }

    private static IEnumerable<string> FrameworkSources()
        => Repository.SourceFiles(Path.Combine(Repository.Root, "src"), "*.cs");

    private static IEnumerable<Type> FrameworkTypes()
        => Directory.EnumerateFiles(AppContext.BaseDirectory, "DigitalBrain*.dll")
            .Select(Assembly.LoadFrom)
            .SelectMany(LoadableTypes);

    private static IEnumerable<Type> LoadableTypes(Assembly assembly)
    {
        try { return assembly.GetTypes(); }
        catch (ReflectionTypeLoadException ex) { return ex.Types.OfType<Type>(); }
    }
}
