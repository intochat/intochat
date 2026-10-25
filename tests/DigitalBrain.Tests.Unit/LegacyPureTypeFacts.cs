using System.Xml.Linq;

namespace DigitalBrain.Tests.Unit;

// The Orleans-free legacy types copied from os IntoChat.OS (03-03); 03-10 deletes the os copies.
public sealed class LegacyPureTypeFacts
{
    [Theory]
    [InlineData("DigitalBrain.Contracts.NeuronContractAttribute")]
    [InlineData("DigitalBrain.Contracts.NeuronAliasAttribute")]
    [InlineData("DigitalBrain.Contracts.FieldAttribute")]
    [InlineData("DigitalBrain.Contracts.DefaultNeuronTypeAttribute")]
    [InlineData("DigitalBrain.Access.AccessAttribute")]
    [InlineData("DigitalBrain.Access.PublicAttribute")]
    [InlineData("DigitalBrain.Access.SharedAttribute")]
    [InlineData("DigitalBrain.Access.PerAccountAttribute")]
    [InlineData("DigitalBrain.Access.InternalAttribute")]
    [InlineData("DigitalBrain.State.ExportableStateAttribute")]
    [InlineData("DigitalBrain.State.StoredRecord")]
    [InlineData("DigitalBrain.State.WriteResult")]
    [InlineData("DigitalBrain.State.IStateCodec")]
    [InlineData("DigitalBrain.Spi.IRuntimeEndpoint")]
    [InlineData("DigitalBrain.Spi.IDeactivatingNeuron")]
    [InlineData("DigitalBrain.Spi.IRemindedNeuron")]
    [InlineData("DigitalBrain.Spi.INeuronTimers")]
    [InlineData("DigitalBrain.Spi.ICallerContextCarrier")]
    public void Copied_type_exists(string fullName)
    {
        var type = typeof(Signal).Assembly.GetType(fullName);
        Assert.NotNull(type);
        Assert.True(type.IsPublic, fullName);
    }

    [Fact]
    public void Alias_helper_is_not_copied()
    {
        Assert.Null(typeof(DigitalBrain.Contracts.NeuronAliasAttribute).GetMethod("Of"));
    }

    [Fact]
    public void Core_still_has_no_project_reference()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "DigitalBrain.slnx"))) { dir = dir.Parent; }
        Assert.NotNull(dir);
        var csproj = XDocument.Load(Path.Combine(dir.FullName, "src", "DigitalBrain", "DigitalBrain.csproj"));
        Assert.Empty(csproj.Descendants("ProjectReference"));
    }
}
