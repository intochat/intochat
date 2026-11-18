using System.Reflection;
using DigitalBrain.Access;

namespace IntoChat.OS.Architecture.Tests.Unit;

// One caller in the os: DigitalBrain.Access.Caller is the only identity a call carries. The old model stays deleted, no
// request-string identity key survives, and no neuron contract or port interface takes a caller as an argument; the
// callee reads the stamped caller. Domain and helper types (CallerRules, HttpCaller, UploadScope.Of) may take a Caller value.
public sealed class OneCallerOsFacts
{
    [Fact]
    public void NoOsSourceNamesCapabilityCaller()
    {
        var offenders = OsSources().Where(path => File.ReadAllText(path).Contains("CapabilityCaller", StringComparison.Ordinal))
            .Select(Repository.Relative).ToList();
        Assert.True(offenders.Count == 0, string.Join(", ", offenders));
    }

    [Fact]
    public void NoOsSourceCarriesARequestStringIdentity()
    {
        var offenders = OsSources().Where(path => File.ReadAllText(path).Contains("db.cap.", StringComparison.Ordinal))
            .Select(Repository.Relative).ToList();
        Assert.True(offenders.Count == 0, string.Join(", ", offenders));
    }

    [Fact]
    public void NoInterfaceMethodTakesACaller()
    {
        Assert.Contains(ProductionArchitecture.Types, type => type.IsInterface && IsContractOrPort(type) && !type.Name.EndsWith("Port", StringComparison.Ordinal));
        var offenders = ProductionArchitecture.Types
            .Where(type => type.IsInterface && type.Namespace?.StartsWith("OrleansCodeGen", StringComparison.Ordinal) != true)
            .Where(IsContractOrPort)
            .SelectMany(type => type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly)
                .Where(method => method.GetParameters().Any(parameter => TakesCaller(parameter.ParameterType)))
                .Select(method => type.FullName + "." + method.Name))
            .Order().ToList();
        Assert.True(offenders.Count == 0, string.Join(", ", offenders));
    }

    // A neuron contract carries [NeuronAlias] (or extends one that does); a port is named *Port. Host-side authority
    // services (IEdgeGrantsCache, ICallFilterStage, IIdentityClient) decide about a caller and may take one, like CallerRules.
    private static bool IsContractOrPort(Type type)
        => type.Name.EndsWith("Port", StringComparison.Ordinal)
            || type.GetCustomAttribute<DigitalBrain.Contracts.NeuronAliasAttribute>() is not null
            || type.GetInterfaces().Any(parent => parent.GetCustomAttribute<DigitalBrain.Contracts.NeuronAliasAttribute>() is not null);

    private static bool TakesCaller(Type type)
        => (type.IsByRef ? type.GetElementType()! : type) == typeof(Caller);

    private static IEnumerable<string> OsSources()
        => Repository.ProjectFiles("*.cs", projects: project => !Repository.TestProjects().Contains(project));
}
