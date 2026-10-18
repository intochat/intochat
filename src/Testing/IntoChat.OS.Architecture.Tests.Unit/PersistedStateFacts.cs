using Orleans;

namespace IntoChat.OS.Architecture.Tests.Unit;

public sealed class PersistedStateFacts
{
    private static readonly Type[] States = PersistedStateRules.Discover(ProductionArchitecture.Types);

    [Fact]
    public void PersistedStateHasUniqueIdsAndConcreteCollections()
    {
        Assert.NotEmpty(States);
        Assert.Contains(States, type => type == typeof(State));
        Assert.Contains(States, type => type.FullName == "IntoChat.OS.Authority.Secrets.SecretsState");
        var violations = States.SelectMany(PersistedStateRules.ShapeViolations).ToArray();
        Assert.True(violations.Length == 0, string.Join(Environment.NewLine, violations));
    }
}
