using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using DigitalBrain.Rules;
using Xunit;

namespace DigitalBrain.Architecture.Tests.Unit;

/// <summary>
/// The house style for aggregates: refusals are values with one stable code each, union cases are concrete so stored state reads back,
/// and neither reaches for a runtime, a task or the clock.
/// </summary>
public sealed partial class DomainModelFacts
{
    [GeneratedRegex("""\)\s*:\s*Refusal\(\s*"(?<code>[^"]*)"\s*\)""")]
    private static partial Regex RefusalCode();

    [GeneratedRegex("^[a-z][a-z0-9]*(\\.[a-z][a-z0-9-]*)+$")]
    private static partial Regex CodeShape();

    // Framework and product sources: a code is a wire contract across both.
    private static IEnumerable<(string File, string Code)> Codes =>
        new[] { Path.Combine(Repository.Root, "src"), Path.GetFullPath(Path.Combine(Repository.Root, "..", "src")) }
            .SelectMany(directory => Repository.SourceFiles(directory, "*.cs"))
            .SelectMany(file => RefusalCode().Matches(File.ReadAllText(file)).Select(match => (file, match.Groups["code"].Value)));

    private static IEnumerable<Type> FrameworkTypes =>
        typeof(DomainModelFacts).Assembly.GetReferencedAssemblies()
            .Where(name => name.Name!.StartsWith("DigitalBrain", StringComparison.Ordinal))
            .Select(Assembly.Load)
            .SelectMany(assembly => assembly.GetTypes());

    private static readonly Type[] Impure = [typeof(Task), typeof(ValueTask), typeof(IServiceProvider), typeof(TimeProvider), typeof(HttpClient), typeof(Neurons.INeuronContext)];

    [Fact]
    public void EveryRefusalCodeIsDottedAndUnique()
    {
        var codes = Codes.ToList();

        Assert.Empty(codes.Where(entry => !CodeShape().IsMatch(entry.Code)).Select(entry => $"{entry.Code} in {entry.File}"));
        Assert.Empty(codes.GroupBy(entry => entry.Code).Where(group => group.Count() > 1)
            .Select(group => $"{group.Key} in {string.Join(", ", group.Select(entry => entry.File))}"));
    }

    [Fact]
    public void RefusalsAndUnionCasesAreSealedAndPure()
    {
        var unions = FrameworkTypes.Where(type => type.IsDefined(typeof(UnionAttribute), inherit: false) && !type.IsGenericTypeDefinition);
        var cases = unions.SelectMany(union => union.GetConstructors().Where(c => c.GetParameters().Length == 1).Select(c => c.GetParameters()[0].ParameterType));
        var refusals = FrameworkTypes.Where(type => type.IsSubclassOf(typeof(Refusal)));
        var subjects = cases.Concat(refusals).Distinct().ToList();

        Assert.Empty(subjects.Where(type => !type.IsSealed).Select(type => $"{type.FullName} is not sealed"));
        Assert.Empty(subjects.SelectMany(type => type.GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
            .Where(property => Impure.Any(impure => impure.IsAssignableFrom(property.PropertyType)))
            .Select(property => $"{type.FullName}.{property.Name} is {property.PropertyType.Name}")));
    }
}
