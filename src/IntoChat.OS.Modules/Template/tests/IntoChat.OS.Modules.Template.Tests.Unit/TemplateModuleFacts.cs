using DigitalBrain.Testing;

namespace IntoChat.OS.Modules.Template.Tests;

public sealed class TemplateModuleFacts : ModuleOnTestOSFacts<TemplateModule>
{
    [Fact]
    public async Task TemplateNeuronEchoesMessageWithPrefix()
    {
        await using var os = TestOS.Local();
        os.Register(typeof(TemplateModule).Assembly);
        var id = new DigitalBrain.NeuronId(new DigitalBrain.BrainId("test"), "template.echo", "probe");
        var neuron = os.Get<Contracts.ITemplateNeuron>(id);
        var result = await neuron.Echo("world", TestContext.Current.CancellationToken);
        Assert.Equal("Echo: world", result);
    }
}
