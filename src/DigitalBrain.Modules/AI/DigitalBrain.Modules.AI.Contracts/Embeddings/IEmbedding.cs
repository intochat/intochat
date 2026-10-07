using DigitalBrain.AI.Models;

namespace DigitalBrain.AI.Embeddings;

// Marker root for embedding model markers; embeddings are resolved as keyed
// IEmbeddingGenerator services, not neurons.
public interface IEmbedding : IAiMarker;
