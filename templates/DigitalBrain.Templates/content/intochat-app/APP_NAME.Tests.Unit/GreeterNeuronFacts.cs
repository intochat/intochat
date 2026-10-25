using System.Text.RegularExpressions;
using DigitalBrain;
using DigitalBrain.Testing;

namespace APP_NAMESPACE.Tests.Unit;

public sealed class GreeterNeuronFacts
{
    [Fact]
    public async Task Greet_publishes_greeted()
    {
        var harness = await NeuronHarness.Create<GreeterNeuron>(new NeuronId(new BrainId("test"), "greeter", "one"), TestContext.Current.CancellationToken);

        await harness.Neuron.Greet("Ada");

        harness.AssertPublished<Greeted>(signal => signal.Name == "Ada");
    }

    [Fact]
    public void App_id_is_lowercase_and_dotted()
    {
        Assert.Matches(new Regex("^[a-z][a-z0-9-]*(\\.[a-z][a-z0-9-]*)*$"), APP_CLASSApp.Id);
    }
}
