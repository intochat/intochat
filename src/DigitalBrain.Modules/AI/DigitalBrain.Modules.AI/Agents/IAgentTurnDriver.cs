namespace DigitalBrain.AI.Agents;

// Start hands the turn to this one-way self-reference, so the caller's request ends while the
// turn runs on the neuron's own scheduler.
internal interface IAgentTurnDriver : IGrainWithStringKey
{
    [Orleans.Concurrency.OneWay] Task Drive(string runId, AgentTurnStart turn);
}
