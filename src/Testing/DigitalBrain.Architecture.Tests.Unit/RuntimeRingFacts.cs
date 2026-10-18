namespace DigitalBrain.Architecture.Tests.Unit;

// The runtime ring (clean-core 2.1): a runtime adapter hosts neurons for a provider. DigitalBrain.Runtime.InMemory sees
// Framework internals (Framework grants it InternalsVisibleTo to attach a context and read the call carriers), so
// nothing but tests may reference it; production composes a runtime through the provider instead.
public sealed class RuntimeRingFacts
{
    private const string InMemory = "DigitalBrain.Runtime.InMemory";

    [Fact]
    public void OnlyTestsReferenceTheInMemoryRuntime()
    {
        var offenders = Repository.Projects.Values
            .Where(project => !project.Name.Contains(".Tests", StringComparison.Ordinal) && Repository.References(project.Path).Contains(InMemory))
            .Select(project => project.Name).Order(StringComparer.Ordinal).ToArray();
        Assert.True(offenders.Length == 0, "Production projects reference the in-memory runtime:\n" + string.Join('\n', offenders));
    }

    [Fact]
    public void TheInMemoryRuntimeReferencesOnlyFramework()
    {
        Assert.Equal(["DigitalBrain.Framework"], Repository.References(Repository.ProjectNamed(InMemory).Path));
    }
}
