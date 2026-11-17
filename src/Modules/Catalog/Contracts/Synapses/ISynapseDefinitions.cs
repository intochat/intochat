using System.ComponentModel;
using DigitalBrain.Contracts;

namespace IntoChat.OS.Modules.Apps.Synapses;

// A synapse is behavior; its form is how it is expressed: a reaction unit (pure functions over a state), a flow (durable steps),
// an Agent Framework workflow (a graph of executors), or an agent (a model with tools).
public enum SynapseForm { Reaction, Flow, Workflow, Agent }

public sealed record SynapseDefinition(
    string Id,
    SynapseForm Form,
    string Name,
    string Description,
    int Runs,
    string? LastRun);

[NeuronAlias("synapse.definitions"), DefaultNeuronType("synapse.definitions")]
[Description("Every piece of behavior in this brain by form (reaction units, flows, workflows, agents) with how often it ran and how its last run ended.")]
public interface ISynapseDefinitions
{
    static DigitalBrain.NeuronId Key(string brain) => new(new DigitalBrain.BrainId(brain), "synapse.definitions", "synapses/definitions");

    [DigitalBrain.Contracts.ReadOnlyCall, DigitalBrain.Contracts.Interleaves]
    Task<SynapseDefinition[]> List();
}
