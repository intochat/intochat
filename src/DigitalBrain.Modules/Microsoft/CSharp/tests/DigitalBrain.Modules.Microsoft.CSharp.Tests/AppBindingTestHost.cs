using DigitalBrain;
using DigitalBrain.Framework;
using DigitalBrain.Microsoft.CSharp;
using Orleans.Runtime;

namespace DigitalBrain.Modules.Microsoft.CSharp.Tests;

[Alias("tests.csharp.binding-host")]
public interface IAppBindingTestHost : INeuron
{
    Task Bind(string file, string? overrideApp = null);
    Task Start(string file);
}

[GrainType(DigitalBrain.Framework.AppGrainType.Name)]
public sealed class AppBindingTestHost : Neuron, IAppBindingTestHost
{
    public override DigitalBrain.Framework.NeuronAccess Access(string operation)
        => DigitalBrain.Framework.NeuronAccess.PublicOperation;
    public Task Bind(string file, string? overrideApp = null)
        => GrainFactory.GetGrain<ICSharpAppBinding>(file).BindApp(overrideApp ?? this.GetPrimaryKeyString());
    public async Task Start(string file)
    {
        var behavior = GrainFactory.GetGrain<ICSharpFile>(file);
        await behavior.Write("Console.WriteLine(1);");
        await behavior.Start();
    }
}
