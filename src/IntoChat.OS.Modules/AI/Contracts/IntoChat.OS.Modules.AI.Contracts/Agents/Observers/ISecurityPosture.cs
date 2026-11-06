using DigitalBrain.Contracts;

namespace IntoChat.OS.Modules.AI.Agents.Observers;

// The brain's security posture as context: an operator can stop every agent that lists security.posture, and a revoked
// assistant grant is noted. Stop writes a trusted interrupt, so it blocks those agents until Resume.
[NeuronAlias("ai.security-posture"), DefaultNeuronType("ai.security-posture")]
public interface ISecurityPosture
{
    Task Stop(string reason);
    Task Resume();
}

// The posture neuron read the assistant's grants (Held is how many are in force).
[DigitalBrain.Signal("ai.security-posture-noted")]
public sealed record SecurityPostureNoted(string Key, int Held) : DigitalBrain.Signal;
