namespace DigitalBrain.Framework.Tests.Integration;

[NeuronAlias("test.neutral-only")]
public interface INeutralAliasedNeuron : INeuron { Task Ping(); }

[NeuronAlias("test.neutral-first"), Alias("test.orleans-second")]
public interface IDoublyAliasedNeuron : INeuron { Task Ping(); }

[Alias("test.orleans-only")]
public interface IOrleansAliasedNeuron : INeuron { Task Ping(); }

[GenerateSerializer, NeuronAlias("test.neutral-signal")]
public sealed record NeutralAliasedSignal : Signal;

[GenerateSerializer, Alias("test.orleans-signal")]
public sealed record OrleansAliasedSignal : Signal;

// PR 3 of the clean-core plan: the neutral alias is read first, the Orleans alias second, and the
// strings every shipped contract resolves to are unchanged.
public sealed class NeuronAliasFacts
{
    [Fact]
    public void TheNeutralAliasWinsOverTheOrleansAlias()
    {
        Assert.Equal("test.neutral-only", NeuronAliases.Of(typeof(INeutralAliasedNeuron)));
        Assert.Equal("test.neutral-first", NeuronAliases.Of(typeof(IDoublyAliasedNeuron)));
        Assert.Equal("test.orleans-only", NeuronAliases.Of(typeof(IOrleansAliasedNeuron)));
    }

    [Fact]
    public void AnUnaliasedTypeHasNoAlias()
        => Assert.Null(NeuronAliases.Of(typeof(NeuronAliasFacts)));

    [Fact]
    public void ASignalIdIsTheSignalsAlias()
    {
        Assert.Equal(new SignalId("test.neutral-signal"), SignalId.Of(typeof(NeutralAliasedSignal)));
        Assert.Equal(new SignalId("test.orleans-signal"), SignalId.Of(typeof(OrleansAliasedSignal)));
        Assert.Throws<ArgumentException>(() => SignalId.Of(typeof(NeuronAliasFacts)));
    }

    [Fact]
    public void CompiledTypesReadTheNeutralAliasFirst()
    {
        var entries = new CompiledTypes([typeof(NeuronAliasFacts).Assembly]).Entries.ToDictionary(entry => entry.Name, entry => entry.Alias, StringComparer.Ordinal);

        Assert.Equal("test.neutral-only", entries[typeof(INeutralAliasedNeuron).FullName!]);
        Assert.Equal("test.neutral-first", entries[typeof(IDoublyAliasedNeuron).FullName!]);
        Assert.Equal("test.orleans-only", entries[typeof(IOrleansAliasedNeuron).FullName!]);
        Assert.Equal("test.neutral-signal", entries[typeof(NeutralAliasedSignal).FullName!]);
        Assert.Equal("test.orleans-signal", entries[typeof(OrleansAliasedSignal).FullName!]);
    }

    // No shipped contract carries a neutral alias yet, so every effective alias is still its Orleans alias.
    [Fact]
    public void ShippedCoreAliasesAreUnchanged()
    {
        Assert.Equal("brain", NeuronAliases.Of(typeof(IBrain)));
        Assert.Equal("brain.signal", NeuronAliases.Of(typeof(Signal)));
        Assert.Equal("signal.id", NeuronAliases.Of(typeof(SignalId)));
    }

    [Fact]
    public void TheNeutralAttributesCarryTheirValues()
    {
        Assert.Equal(3u, new FieldAttribute(3).Id);
        Assert.Equal("brain", new DefaultNeuronTypeAttribute("brain").Type);
        Assert.Equal("IsReadOnly", new InterleavesWhenAttribute("IsReadOnly").Predicate);
        Assert.Equal("00:05:00", new CallTimeoutAttribute("00:05:00").Timeout);
    }
}
