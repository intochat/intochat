using Orleans;

namespace IntoChat.OS.Architecture.Tests.Unit;

public sealed partial class PersistedStateFacts
{
    private static readonly Type[] States = PersistedStateRules.Discover(ProductionArchitecture.Types);

    [Fact]
    public void PersistedStateHasUniqueIdsAndConcreteCollections()
    {
        Assert.NotEmpty(States);
        Assert.Contains(States, type => type.FullName == "IntoChat.OS.Modules.Flutter.Workspace.WorkspaceStorage");
        Assert.Contains(States, type => type.FullName == "IntoChat.OS.Authority.Secrets.SecretsState");
        var violations = States.SelectMany(PersistedStateRules.ShapeViolations).ToArray();
        Assert.True(violations.Length == 0, string.Join(Environment.NewLine, violations));
    }

    // The members that still hold a concrete collection only shrink: a migrated member leaves the baseline, a new
    // one is refused (ShapeViolations above), and an entry naming nothing is stale.
    [Fact]
    public void ConcreteCollectionBaselineHoldsExactlyTheUnmigratedMembers()
    {
        var present = States.SelectMany(PersistedStateRules.ConcreteCollectionMembers).ToHashSet(StringComparer.Ordinal);
        var stale = PersistedStateRules.Baseline.Where(name => !present.Contains(name)).Order(StringComparer.Ordinal).ToArray();
        Assert.True(stale.Length == 0, "Migrated members linger in PersistedStateCollectionBaseline.json; remove: " + string.Join(", ", stale));
    }
}
