namespace IntoChat.OS.Modules.Apps.Tests.Apps;

// Guards in other modules recognize the calling app by grain type; the neuron must declare
// exactly the type they check.
public sealed class AppGrainTypeFacts
{
    [Fact]
    public void TheAppNeuronDeclaresTheGrainTypeTheGuardsCheck()
    {
        var neuron = typeof(AppsModule).Assembly.GetType("IntoChat.OS.Modules.Apps.AppNeuron", throwOnError: true)!;
        // 06-08: a plain neuron; the guards read its type from the runtime host grain's neuron id.
        var declared = (DigitalBrain.NeuronAttribute)Attribute.GetCustomAttribute(neuron, typeof(DigitalBrain.NeuronAttribute))!;
        Assert.Equal(IntoChat.OS.Connections.AppGrainType.Name, declared.Type);
    }
}
