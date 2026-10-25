using IntoChat.OS.Modules.AI.Inference;
using IntoChat.OS.Connections;
using IntoChat.OS.Framework;
using IntoChat.OS.Sdk;

namespace IntoChat.OS.Architecture.Tests.Unit;

public sealed class PersistedStateRulesFacts
{
    [Fact]
    public void PositionalRecordMembersUseTheirOwnImplicitSerializationLayer()
    {
        Assert.Empty(PersistedStateRules.ShapeViolations(typeof(PositionalState)));
        Assert.Empty(PersistedStateRules.ShapeViolations(typeof(PositionalStruct)));
        Assert.Empty(PersistedStateRules.ShapeViolations(typeof(MixedRecord)));
        Assert.Contains(PersistedStateRules.ShapeViolations(typeof(RecordWithBodyMember)),
            violation => violation.Contains("Unnumbered: persisted member has no Id", StringComparison.Ordinal));
        Assert.NotEmpty(PersistedStateRules.ShapeViolations(typeof(DisabledPositionalState)));
        Assert.NotEmpty(PersistedStateRules.ShapeViolations(typeof(OrdinaryConstructorState)));
    }

    [Fact]
    public void ImplicitRecordMembersAreTraversedAndTheirCollectionsValidated()
    {
        Assert.Contains(PersistedStateRules.ShapeViolations(typeof(PositionalList)),
            violation => violation.Contains("persisted collections", StringComparison.Ordinal));
        var states = PersistedStateRules.Discover([typeof(PositionalOwner), typeof(PositionalState), typeof(GrainState)]);
        Assert.Contains(typeof(PositionalState), states);
        Assert.Contains(typeof(GrainState), states);
        Assert.Contains(states.SelectMany(PersistedStateRules.ShapeViolations),
            violation => violation.Contains("GrainState.Values: persisted collections", StringComparison.Ordinal));
    }

    [Fact]
    public void ConcreteCollectionsAreRefusedUnlessBaselinedOrLegacyShape()
    {
        var violations = PersistedStateRules.ShapeViolations(typeof(ConcreteCollections)).ToArray();
        foreach (var member in new[] { "Map", "Items", "Set", "Sorted", "Line", "Pile", "Nested" })
        {
            Assert.Contains(violations, violation => violation.Contains($"ConcreteCollections.{member}: persisted collections must be arrays", StringComparison.Ordinal));
        }
        Assert.DoesNotContain(violations, violation => violation.Contains("ConcreteCollections.Array", StringComparison.Ordinal));
        Assert.DoesNotContain(violations, violation => violation.Contains("ConcreteCollections.Older", StringComparison.Ordinal));
        Assert.Contains(violations, violation => violation.Contains("ConcreteCollections.Unmarked", StringComparison.Ordinal));
    }

    [GenerateSerializer]
    internal sealed class ConcreteCollections
    {
        [Id(0)] public Dictionary<string, string> Map { get; set; } = [];
        [Id(1)] public List<string> Items { get; set; } = [];
        [Id(2)] public HashSet<string> Set { get; set; } = [];
        [Id(3)] public SortedDictionary<string, string> Sorted { get; set; } = [];
        [Id(4)] public Queue<string> Line { get; set; } = [];
        [Id(5)] public Stack<string> Pile { get; set; } = [];
        [Id(6)] public StringEntry[] Array { get; set; } = [];
        [Id(7), LegacyShape] public Dictionary<string, string>? Older { get; set; }
        [Id(8)] public List<string>[] Nested { get; set; } = [];
        [Id(9)] public Dictionary<string, string[]>? Unmarked { get; set; }
    }

    private sealed class PositionalOwner(IPersistentState<PositionalState> state)
    {
        public IPersistentState<PositionalState> State { get; } = state;
    }

    [GenerateSerializer]
    internal sealed record PositionalState(GrainState Nested);

    [GenerateSerializer]
    internal readonly record struct PositionalStruct(int Value);

    [GenerateSerializer]
    internal sealed record MixedRecord(string Positional)
    {
        [Id(0)] public int Explicit { get; init; }
    }

#pragma warning disable ORLEANS0004 // Deliberately invalid shapes exercise the architectural guard.
    [GenerateSerializer]
    internal sealed record RecordWithBodyMember(string Positional)
    {
        public int Unnumbered { get; init; }
    }

    [GenerateSerializer(IncludePrimaryConstructorParameters = false)]
    internal sealed record DisabledPositionalState(string Value)
    {
        [Id(0)] public int Explicit { get; init; }
    }
#pragma warning restore ORLEANS0004

    [GenerateSerializer]
    internal sealed record PositionalList(IReadOnlyList<string> Values);

    private sealed class OrdinaryConstructorState(string value)
    {
        public string Value { get; set; } = value;
    }

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
