using DigitalBrain.Capabilities.Compute;
using IntoChat.OS.Modules.AI.Models;

namespace IntoChat.OS.Modules.AI.Pricing;

// The AI module's own list prices: per concrete model, four token classes priced per million tokens.
// Local models are priced at zero. Micros per million tokens = usd_per_million_tokens * 1_000_000.
internal sealed class ModelPriceList : IPriceList
{
    private const decimal MillionTokens = 1_000_000m;
    private static readonly Lazy<Dictionary<string, long>> MicrosPerMillion = new(Load);

    public long? MicrosPerUnit(CostKind kind, string item)
    {
        if (kind != CostKind.Model || string.IsNullOrWhiteSpace(item)) { return null; }
        return MicrosPerMillion.Value.TryGetValue(item, out var micros) ? micros : null;
    }

    private static Dictionary<string, long> Load()
    {
        var prices = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var card in KnownModels.All)
        {
            if (card.Price is not { } price) { continue; }
            var id = card.Id.Value;
            prices[$"{id}:input"] = ToMicros(price.InputPerMTok);
            prices[$"{id}:cached"] = ToMicros(price.CachedInputPerMTok ?? 0m);
            prices[$"{id}:reasoning"] = ToMicros(price.ReasoningPerMTok ?? 0m);
            prices[$"{id}:output"] = ToMicros(price.OutputPerMTok);
        }

        return prices;
    }

    private static long ToMicros(decimal usdPerMillionTokens) => (long)(usdPerMillionTokens * MillionTokens);
}
