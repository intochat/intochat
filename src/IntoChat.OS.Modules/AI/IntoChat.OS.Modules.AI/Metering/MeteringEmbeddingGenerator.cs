using IntoChat.OS.Framework.Compute;
using Microsoft.Extensions.AI;

namespace IntoChat.OS.AI.Metering;

// Prices the provider's reported input tokens for embedding calls through the kernel's cost meter.
// Embeddings are their own row: the item carries the embedding model id, not a chat model's.
internal sealed class MeteringEmbeddingGenerator(
    IEmbeddingGenerator<string, Embedding<float>> innerGenerator, ICostMeter meter, Funding funding, string model)
    : DelegatingEmbeddingGenerator<string, Embedding<float>>(innerGenerator)
{
    public override async Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(
        IEnumerable<string> values, EmbeddingGenerationOptions? options = null, CancellationToken cancellationToken = default)
    {
        var embeddings = await base.GenerateAsync(values, options, cancellationToken).ConfigureAwait(false);
        if (embeddings.Usage?.InputTokenCount is { } input)
        {
            var id = Guid.NewGuid().ToString("N");
            try
            {
                await meter.RecordAsync(CostKind.Model, $"{model}:input", input / 1_000_000m, "mtok", funding, $"{id}:input", cancellationToken).ConfigureAwait(false);
            }
            catch { /* Metering is an observer: a ledger fault must never fail the embedding call. */ }
        }
        return embeddings;
    }
}
