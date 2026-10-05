using Orleans.Runtime;

namespace DigitalBrain.Apps.Tests.Apps;

// Guards in other modules recognize the calling app by grain type; the neuron must declare
// exactly the type they check.
public sealed class AppGrainTypeFacts
{
    [Fact]
    public void TheAppNeuronDeclaresTheGrainTypeTheGuardsCheck()
    {
        var neuron = typeof(AppsModule).Assembly.GetType("DigitalBrain.Apps.AppNeuron", throwOnError: true)!;
        var declared = (GrainTypeAttribute)Attribute.GetCustomAttribute(neuron, typeof(GrainTypeAttribute))!;
        Assert.Equal(DigitalBrain.Framework.AppGrainType.Name, declared.GetGrainType(null!, neuron).ToString());
    }
}
