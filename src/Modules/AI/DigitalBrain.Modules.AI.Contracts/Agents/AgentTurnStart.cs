namespace DigitalBrain.AI.Agents;

// What a script asks of one agent turn. Model is empty for the deployment default, a catalog id,
// or "scripted/<key>"; Conversation is the IAgent that keeps the transcript.
[GenerateSerializer, Alias("ai.turn-start")]
public sealed record AgentTurnStart(
    [property: Id(0)] string Conversation,
    [property: Id(1)] string Model,
    [property: Id(2)] string SystemPrompt,
    [property: Id(3)] string[] Tools,
    [property: Id(4)] string Message);
