using DigitalBrain;
using DigitalBrain.Contracts;

namespace IntoChat.OS.Modules.AI.Venn;

/// <summary>One recorded historical decision precedent in the brain's Venn Outcome Store.</summary>
[GenerateSerializer, Alias("ai.venn.precedent")]
public sealed record VennPrecedent(
    [property: Id(0)] string Id,
    [property: Id(1)] string Domain,
    [property: Id(2)] string ContextSummary,
    [property: Id(3)] float[] Embedding,
    [property: Id(4)] string Action,
    [property: Id(5)] double OutcomeScore,
    [property: Id(6)] DateTimeOffset RecordedAt);

/// <summary>Compiled lightweight System 1 class prototype (centroid + positive weight) for &lt;1ms local inference.</summary>
[GenerateSerializer, Alias("ai.venn.prototype")]
public sealed record VennClassPrototype(
    [property: Id(0)] string Action,
    [property: Id(1)] float[] Centroid,
    [property: Id(2)] int SampleCount,
    [property: Id(3)] double MeanOutcome);

/// <summary>Durable state of a brain's personal Venn System 1 decision engine.</summary>
[GenerateSerializer, Alias("ai.venn.state")]
public sealed record VennSystemOneState(
    [property: Id(0)] int Version = 0,
    [property: Id(1)] int CompiledVersion = 0,
    [property: Id(2)] double AbstentionThreshold = 0.55,
    [property: Id(3)] VennPrecedent[] Precedents = null!,
    [property: Id(4)] VennClassPrototype[] Prototypes = null!)
{
    public VennSystemOneState() : this(0, 0, 0.55, [], []) { }
}

/// <summary>Typed Decisions-API Choice result with explicit abstention ("none of these" → escalate to System 2).</summary>
[GenerateSerializer, Alias("ai.venn.choice-result")]
public sealed record VennChoiceResult(
    [property: Id(0)] string? ChosenAction,
    [property: Id(1)] double Confidence,
    [property: Id(2)] bool Abstained,
    [property: Id(3)] Dictionary<string, double> Scores);

/// <summary>Typed Decisions-API Score result (continuous outcome prediction).</summary>
[GenerateSerializer, Alias("ai.venn.score-result")]
public sealed record VennScoreResult(
    [property: Id(0)] double PredictedScore,
    [property: Id(1)] double Confidence,
    [property: Id(2)] bool Abstained,
    [property: Id(3)] int PrecedentCount);

/// <summary>Typed Decisions-API Noul result (yes/no probability with abstention).</summary>
[GenerateSerializer, Alias("ai.venn.noul-result")]
public sealed record VennNoulResult(
    [property: Id(0)] bool Positive,
    [property: Id(1)] double Probability,
    [property: Id(2)] bool Abstained);

[Signal("ai.venn.precedent-recorded")]
public sealed record VennPrecedentRecorded(string NeuronKey, int Version, string Domain, string Action, double OutcomeScore) : Signal;

[Signal("ai.venn.head-compiled")]
public sealed record VennHeadCompiled(string NeuronKey, int CompiledVersion, int PrototypeCount, int PrecedentCount) : Signal;

/// <summary>
/// S11 — System 1 Vector Decision Model (Venn VDM) neuron.
/// Separates frozen embeddings (Reader: EmbeddingGemma / HybridMemoryEmbedder) from a fast personal
/// Outcome Store + compiled Decision Head (&lt;1ms local Choice / Score / Noul)
/// so the agent learns continuously from every interaction and escalates to System 2
/// only when it abstains.
/// </summary>
[NeuronAlias("ai.venn")]
[DefaultNeuronType("ai.venn")]
public interface IVennSystemOne
{
    static NeuronId DefaultKey(BrainId brain, string key = "default") => new(brain, "ai.venn", key);

    Task<VennSystemOneState> RecordOutcome(string domain, string contextSummary, float[] embedding, string action, double outcomeScore = 1.0);
    Task<VennSystemOneState> LearnFromText(string domain, string contextText, string action, double outcomeScore = 1.0, bool autoCompile = true);
    Task<VennSystemOneState> CompileHead(double? abstentionThreshold = null);

    [ReadOnlyCall]
    Task<VennChoiceResult> Choose(float[] queryEmbedding, string[]? candidateActions = null);

    [ReadOnlyCall]
    Task<VennChoiceResult> ChooseForText(string contextText, string[]? candidateActions = null);

    [ReadOnlyCall]
    Task<VennScoreResult> Score(float[] queryEmbedding);

    [ReadOnlyCall]
    Task<VennScoreResult> ScoreForText(string contextText);

    [ReadOnlyCall]
    Task<VennNoulResult> Noul(float[] queryEmbedding, string positiveAction);

    [ReadOnlyCall]
    Task<VennNoulResult> NoulForText(string contextText, string positiveAction);

    [ReadOnlyCall]
    Task<VennSystemOneState> ReadStatus();
}
