namespace DigitalBrain.Components;

/// <summary>Reports which neuron types are hosted in the current runtime.</summary>
public interface IHostedNeuronTypes
{
    /// <summary>Returns whether a neuron class is hosted for <paramref name="neuronType"/>.</summary>
    bool IsHosted(string neuronType);
}
