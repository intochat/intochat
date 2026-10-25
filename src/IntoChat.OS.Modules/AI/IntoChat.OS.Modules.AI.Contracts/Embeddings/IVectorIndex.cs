using System.ComponentModel;
using IntoChat.OS;
using Orleans.Concurrency;
using ReadOnly = Orleans.Concurrency.ReadOnlyAttribute;

namespace IntoChat.OS.Modules.AI.Embeddings;

// The caller embeds (IEmbeddingModel) and the index ranks. An empty index takes its dimensions from the next
// write; while it holds entries, every vector it is given or asked about must have those dimensions.
[Alias("ai.vector-index"), Orleans.Metadata.DefaultGrainType("ai.vector-index")]
[Description("A brain's nearest-neighbour memory, keyed {brain}/memory/{name}: entries of id, vector and payload, ranked by cosine similarity.")]
public interface IVectorIndex : INeuron
{
    [Description("Rebuild the index from exactly these entries; entries not given are dropped and the dimensions are taken from them.")]
    Task<VectorIndexSnapshot> Replace(VectorEntry[] entries);

    [Description("Add these entries, replacing any stored entry with the same id.")]
    Task<VectorIndexSnapshot> Upsert(VectorEntry[] entries);

    [Description("Drop the entries with these ids; unknown ids are ignored.")]
    Task<VectorIndexSnapshot> Remove(string[] ids);

    [Description("The take entries nearest the query by cosine similarity, best first, among those whose payload holds every mustMatch pair.")]
    [ReadOnly, AlwaysInterleave]
    Task<VectorMatch[]> Nearest(float[] query, int take, Dictionary<string, string>? mustMatch = null);

    [Description("How many entries the index holds, their dimensions and its revision.")]
    [ReadOnly]
    Task<VectorIndexSnapshot> Read();

    static string Key(string brainId, string name) => $"{brainId}/memory/{name}";
}

[GenerateSerializer, Alias("ai.vector-entry")]
public sealed record VectorEntry(
    [property: Id(0)] string Id,
    [property: Id(1)] float[] Vector,
    [property: Id(2)] Dictionary<string, string> Payload);

[GenerateSerializer, Alias("ai.vector-match")]
public sealed record VectorMatch(
    [property: Id(0)] string Id,
    [property: Id(1)] double Score,
    [property: Id(2)] Dictionary<string, string> Payload);

[GenerateSerializer, Alias("ai.vector-index-snapshot")]
public sealed record VectorIndexSnapshot(
    [property: Id(0)] int Count,
    [property: Id(1)] int Dimensions,
    [property: Id(2)] long Revision);
