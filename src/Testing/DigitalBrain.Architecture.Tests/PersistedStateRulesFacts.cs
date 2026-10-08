using DigitalBrain.AI.Inference;

namespace DigitalBrain.Architecture.Tests;

public sealed class PersistedStateRulesFacts
{
    [Fact]
    public void PublishedMessageContentIsTheOnlyCompatibleCollectionException()
    {
        Assert.Empty(PersistedStateRules.ShapeViolations(typeof(AiMessage)));
        Assert.NotEmpty(PersistedStateRules.ShapeViolations(typeof(OtherMessage)));
        Assert.NotEmpty(PersistedStateRules.ShapeViolations(typeof(InterfaceListState)));
        Assert.NotEmpty(PersistedStateRules.ShapeViolations(typeof(NestedListState)));
    }

    [Fact]
    public void PublishedMessageContentSnapshotsConstructorAndInitCollections()
    {
        var source = new List<AiContent> { new AiText("retained") };
        IReadOnlyList<AiContent> expression = [new AiText("expression")];
        var constructed = new AiMessage("user", source);
        var initialized = new AiMessage("user", []) { Content = source };
        var copied = constructed with { Content = expression };
        source.Clear();

        Assert.Equal(new AiText("retained"), Assert.Single(Assert.IsType<AiContent[]>(constructed.Content)));
        Assert.Equal(new AiText("retained"), Assert.Single(Assert.IsType<AiContent[]>(initialized.Content)));
        Assert.Equal(new AiText("expression"), Assert.Single(Assert.IsType<AiContent[]>(copied.Content)));
    }

    private sealed class OtherMessage
    {
        [Id(1)] public IReadOnlyList<AiContent> Content { get; set; } = [];
    }

    [Theory]
    [InlineData(typeof(DirectStateGrain))]
    [InlineData(typeof(InheritedStateGrain))]
    public void GrainBaseStateAndItsNestedMembersAreValidated(Type grainType)
    {
        var states = PersistedStateRules.Discover(
            [grainType, typeof(GrainState), typeof(PolymorphicState), typeof(Content), typeof(TextContent)]);
        Assert.Contains(typeof(GrainState), states);
        Assert.Contains(typeof(PolymorphicState), states);
        Assert.Contains(typeof(TextContent), states);
        Assert.Contains(states.SelectMany(PersistedStateRules.ShapeViolations),
            violation => violation.Contains("GrainState.Values: persisted collections", StringComparison.Ordinal));
    }

    internal sealed class DirectStateGrain : Grain<GrainState>;
    internal abstract class StateGrain<TState> : Grain<TState> where TState : class, new();
    internal sealed class InheritedStateGrain : StateGrain<GrainState>;

    [GenerateSerializer]
    internal sealed class GrainState
    {
        [Id(0)] public IReadOnlyList<string> Values { get; set; } = [];
        [Id(1)] public PolymorphicState Nested { get; set; } = new();
    }

    [Fact]
    public void PersistedPolymorphicMembersIncludeTheirConcreteSerializedTypes()
    {
        var states = PersistedStateRules.Discover([typeof(StateOwner), typeof(PolymorphicState), typeof(Content), typeof(TextContent)]);
        Assert.Contains(typeof(PolymorphicState), states);
        Assert.Contains(typeof(Content), states);
        Assert.Contains(typeof(TextContent), states);
    }

    private sealed class StateOwner
    {
        public StateOwner(IPersistentState<PolymorphicState> _) { }
    }

    [GenerateSerializer]
    internal sealed class PolymorphicState
    {
        [Id(0)] public Content[] Items { get; set; } = [];
    }

    [GenerateSerializer]
    internal abstract class Content;

    [GenerateSerializer]
    internal sealed class TextContent : Content
    {
        [Id(0)] public string Text { get; set; } = "";
    }

    [Fact]
    public void DuplicateAndMissingIdsAreRejected()
    {
        Assert.NotEmpty(PersistedStateRules.ShapeViolations(typeof(DuplicateIds)));
        Assert.NotEmpty(PersistedStateRules.ShapeViolations(typeof(MissingId)));
        Assert.Empty(PersistedStateRules.ShapeViolations(typeof(ValidState)));
    }

    [Fact]
    public void InterfaceListsAreRejectedInsideNestedCollections()
    {
        Assert.NotEmpty(PersistedStateRules.ShapeViolations(typeof(InterfaceListState)));
        Assert.NotEmpty(PersistedStateRules.ShapeViolations(typeof(NestedListState)));
        Assert.Empty(PersistedStateRules.ShapeViolations(typeof(ValidState)));
    }

    private sealed class DuplicateIds
    {
        [Id(0)] public int First { get; set; }
        [Id(0)] public int Second { get; set; }
    }

    private sealed class MissingId
    {
        public int Value { get; set; }
    }

    private sealed class ValidState
    {
        [Id(0)] public string[] Values { get; set; } = [];
    }

    private sealed class InterfaceListState
    {
        [Id(0)] public IReadOnlyList<string> Values { get; set; } = [];
    }

    private sealed class NestedListState
    {
        [Id(0)] public Dictionary<string, IReadOnlyList<string>> Values { get; set; } = [];
    }
}
