using DigitalBrain.Contracts;

namespace IntoChat.OS.Modules.Template.Contracts;

/// <summary>Canonical template neuron contract.</summary>
[NeuronAlias("template.echo"), DefaultNeuronType("template.echo")]
public interface ITemplateNeuron
{
    /// <summary>Echoes the given message.</summary>
    Task<string> Echo(string message, CancellationToken ct = default);
}
