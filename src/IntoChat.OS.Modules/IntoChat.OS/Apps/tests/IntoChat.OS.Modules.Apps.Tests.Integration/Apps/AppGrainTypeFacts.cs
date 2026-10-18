using Orleans.Runtime;

namespace IntoChat.OS.Apps.Tests.Apps;

// Guards in other modules recognize the calling app by grain type; the neuron must declare
// exactly the type they check.
public sealed class AppGrainTypeFacts
{
    [Fact]
    public void TheAppNeuronDeclaresTheGrainTypeTheGuardsCheck()
    {
        var neuron = typeof(AppsModule).Assembly.GetType("IntoChat.OS.Apps.AppNeuron", throwOnError: true)!;
        var declared = (GrainTypeAttribute)Attribute.GetCustomAttribute(neuron, typeof(GrainTypeAttribute))!;
        Assert.Equal(IntoChat.OS.Framework.AppGrainType.Name, declared.GetGrainType(null!, neuron).ToString());
    }
}
