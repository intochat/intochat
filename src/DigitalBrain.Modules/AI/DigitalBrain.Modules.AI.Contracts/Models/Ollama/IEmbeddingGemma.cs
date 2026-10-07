using DigitalBrain.AI.Embeddings;

namespace DigitalBrain.AI.Models.Ollama;

public sealed class EmbeddingGemma : EmbeddingModel<IEmbeddingGemma>
{
    public override string Id => "embeddinggemma";

    public override AiProvider Provider => AiProvider.Ollama;

    public override int Dimensions => 768;
}

public interface IEmbeddingGemma : IEmbedding;
