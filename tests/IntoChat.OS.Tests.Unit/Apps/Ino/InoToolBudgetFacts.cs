using DigitalBrain.Contracts;
using DigitalBrain.Runtime.Http.Surface;
using IntoChat.OS.Apps.Ino;
using IntoChat.OS.Host.Modules;
using IntoChat.OS.Kernel;
using IntoChat.OS.Modules.Apps;

namespace IntoChat.OS.Tests.Unit.Apps.Ino;

public sealed class InoToolBudgetFacts(ITestOutputHelper output)
{
    private static readonly IContractAssemblySource ContractAssemblies = new ModuleInventory(OperatingSystemModules.Modules);

    internal static ToolSchema[] InoTools() =>
    [
        .. InoAgentDefinition.For(new UserSettingsState()).Contracts
            .Select(static name => LoadedContracts.Find(name, ContractAssemblies))
            .SelectMany(ToolSchemas.Of),
    ];

    [Fact]
    public void EveryInoContractHasTools() =>
        Assert.All(
            InoAgentDefinition.For(new()).Contracts,
            c => Assert.NotEmpty(ToolSchemas.Of(LoadedContracts.Find(c, ContractAssemblies))));

    [Fact]
    public void InoToolNamesAreUniqueAndShort() =>
        Assert.All(
            InoTools().GroupBy(static t => t.Name, StringComparer.Ordinal),
            g =>
            {
                Assert.Single(g);
                Assert.True(g.Key.Length <= 64, g.Key);
            });

    [Fact]
    public void InoToolBudgetIsRecorded()
    {
        var tools = InoTools();
        var bytes = tools.Sum(static t => t.Name.Length + t.Description.Length + t.ParametersJson.Length);
        output.WriteLine($"{tools.Length} tools, {bytes} bytes");
        Assert.InRange(tools.Length, 90, 140);
    }

    [Fact]
    public void RenamedContractsKeepTheirStoredNeuronType()
    {
        Assert.Equal("ino.files", PlainNeuronContracts.TypeOf(typeof(IFiles)));
        Assert.Equal("ino.focus", PlainNeuronContracts.TypeOf(typeof(IFocus)));
        Assert.Equal("ino.data", PlainNeuronContracts.TypeOf(typeof(IData)));
        Assert.Equal("ino.studio", PlainNeuronContracts.TypeOf(typeof(IStudio)));
    }

    [Fact]
    public void FocusPointIsATool() =>
        Assert.Contains(InoTools(), static t => t.Name == "focus_point" && t.DefaultKey == "ino/focus");
}
