using System.Reflection;
using DigitalBrain.Capabilities.Storage;
using DigitalBrain.Contracts;
using DigitalBrain.Neurons;
using DigitalBrain.Runtime.Http.Surface;

namespace DigitalBrain.Runtime.Http.Tests.Unit;

public sealed class LoadedContractsFacts
{
    [NeuronAlias("probe.new", Formerly = ["probe.old", "Old.Ns.IProbe"]), DefaultNeuronType("probe")]
    public interface IFormerProbe
    {
        Task Ping();
    }

    [NeuronAlias("probe.hosted")]
    public interface IHostedProbe
    {
        Task Ping();
    }

    [Neuron("probe.hosted.impl")]
    public sealed class HostedProbeNeuron : Neuron, IHostedProbe
    {
        public Task Ping() => Task.CompletedTask;
    }

    [NeuronAlias("probe.internal", Formerly = ["Old.Ns.IInternalProbe"])]
    internal interface IInternalProbe
    {
        Task Ping();
    }

    private sealed class StaticAssemblySource(params Assembly[] assemblies) : IContractAssemblySource
    {
        public IReadOnlyList<Assembly> ContractAssemblies() => assemblies;
    }

    [Fact]
    public void Finds_contract_from_loaded_assemblies()
    {
        var found = LoadedContracts.Find(typeof(IStorage).FullName!);
        Assert.Equal(typeof(IStorage), found);
    }

    [Fact]
    public void Finds_contract_with_exact_assembly_names_and_rejects_prefix_spoofs()
    {
        var found = LoadedContracts.Find(typeof(IStorage).FullName!, ["CustomAssembly", "DigitalBrain"]);
        Assert.Equal(typeof(IStorage), found);

        // Exact-name matching must NOT treat "DigitalBrain" as a prefix matching "DigitalBrain.Tests.Unit".
        Assert.Null(LoadedContracts.TryFind(typeof(IFormerProbe).FullName!, ["DigitalBrain"]));
        Assert.Null(LoadedContracts.TryFind(typeof(IStorage).FullName!, ["DigitalBrainX"]));
        Assert.Throws<TypeLoadException>(() => LoadedContracts.Find(typeof(IFormerProbe).FullName!, ["DigitalBrain"]));
        Assert.Throws<TypeLoadException>(() => LoadedContracts.Find("NonExistent.MissingContract", ["CustomAssembly"]));
    }

    [Fact]
    public void Finds_contract_through_IContractAssemblySource_only_within_declared_assemblies()
    {
        var storageOnly = new StaticAssemblySource(typeof(IStorage).Assembly);
        var testsOnly = new StaticAssemblySource(typeof(IFormerProbe).Assembly);

        Assert.Equal(typeof(IStorage), LoadedContracts.Find(typeof(IStorage).FullName!, storageOnly));
        Assert.Null(LoadedContracts.TryFind(typeof(IFormerProbe).FullName!, storageOnly));

        Assert.Equal(typeof(IFormerProbe), LoadedContracts.Find("probe.old", testsOnly));
        Assert.Equal(typeof(IFormerProbe).FullName, LoadedContracts.CurrentName("Old.Ns.IProbe", testsOnly));
        Assert.Null(LoadedContracts.TryFind(typeof(IStorage).FullName!, testsOnly));
    }

    [Fact]
    public void FormerNameAndFormerAliasFindTheProbe()
    {
        var testAssemblyName = typeof(IFormerProbe).Assembly.GetName().Name!;
        Assert.Equal(typeof(IFormerProbe), LoadedContracts.Find("Old.Ns.IProbe"));
        Assert.Equal(typeof(IFormerProbe), LoadedContracts.Find("probe.old"));
        Assert.Equal(typeof(IFormerProbe).FullName, LoadedContracts.CurrentName("probe.old"));
        Assert.Equal(typeof(IFormerProbe).FullName, LoadedContracts.CurrentName("Old.Ns.IProbe"));
        Assert.Equal(typeof(IFormerProbe).FullName, LoadedContracts.CurrentName("Old.Ns.IProbe", ["CustomAssembly", testAssemblyName]));
        Assert.Equal("Old.Ns.IProbe", LoadedContracts.CurrentName("Old.Ns.IProbe", ["DigitalBrain"]));
        Assert.Equal(typeof(IFormerProbe).FullName, LoadedContracts.CurrentName(typeof(IFormerProbe).FullName!));
        Assert.Equal("Unknown.Ns.IContract", LoadedContracts.CurrentName("Unknown.Ns.IContract"));
        Assert.Equal("Unknown.Ns.IContract", LoadedContracts.CurrentName("Unknown.Ns.IContract", ["CustomAssembly"]));
        string[] inputs = ["probe.old"];
        Assert.Equal([typeof(IFormerProbe).FullName], inputs.Select(LoadedContracts.CurrentName));
    }

    [Fact]
    public void TryFindInterfaceAndResolveNeuronTypeHandleInternalAndHostedContracts()
    {
        var source = new StaticAssemblySource(typeof(IInternalProbe).Assembly);
        Assert.Same(typeof(IInternalProbe), LoadedContracts.TryFindInterface("probe.internal", source));
        Assert.Same(typeof(IInternalProbe), LoadedContracts.TryFindInterface("Old.Ns.IInternalProbe", source));
        Assert.Same(typeof(IInternalProbe), LoadedContracts.TryFindInterface(typeof(IInternalProbe).FullName!, source));

        Assert.Equal("probe", LoadedContracts.ResolveNeuronType(typeof(IFormerProbe)));
        Assert.Equal("probe.hosted.impl", LoadedContracts.ResolveNeuronType(typeof(IHostedProbe), [typeof(HostedProbeNeuron).Assembly]));
    }
}
