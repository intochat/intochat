namespace DigitalBrain.Components;

/// <summary>Reports which neuron types and contracts are hosted in the current runtime.</summary>
public interface IHostedNeuronTypes
{
    /// <summary>Returns whether a neuron class is hosted for <paramref name="neuronType"/>.</summary>
    bool IsHosted(string neuronType);

    /// <summary>Returns whether any hosted neuron class implements <paramref name="contract"/>.</summary>
    bool IsContractHosted(Type contract) => false;

    /// <summary>Returns the hosted neuron class for <paramref name="neuronType"/>, if known.</summary>
    bool TryGetNeuronClass(string neuronType, out Type? neuronClass)
    {
        neuronClass = null;
        return false;
    }
}
