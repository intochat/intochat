using System.ComponentModel;
using DigitalBrain;

namespace DigitalBrain.AI.Embeddings.Signals;

[GenerateSerializer, Alias("ai.vector-index-changed")]
[Description("A vector index's entries changed: its key, its new revision and how many entries it holds.")]
public sealed record VectorIndexChanged(
    [property: Id(0)] string Index,
    [property: Id(1)] long Revision,
    [property: Id(2)] int Count) : Signal;
