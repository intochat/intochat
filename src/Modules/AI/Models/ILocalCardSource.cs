using System.Runtime.CompilerServices;
using IntoChat.OS.Modules.AI.Models.Ollama;

namespace IntoChat.OS.Modules.AI.Models;

// Seam that S1b plugs ILocalRuntime adapters into; in S1 a single adapter wraps ILocalOllamaProbe.
internal interface ILocalCardSource
{
    IAsyncEnumerable<ModelCard> Installed(CancellationToken ct);
}

internal sealed class OllamaLocalCardSource(ILocalOllamaProbe? probe = null) : ILocalCardSource
{
    public async IAsyncEnumerable<ModelCard> Installed([EnumeratorCancellation] CancellationToken ct)
    {
        if (probe is null)
        {
            yield break;
        }

        var detected = await probe.Detect(ct).ConfigureAwait(false);
        if (detected is null || detected.InstalledModels.Length == 0)
        {
            yield break;
        }

        foreach (var rawTag in detected.InstalledModels)
        {
            ct.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(rawTag))
            {
                continue;
            }

            var tag = rawTag.Trim();
            if (KnownModels.Find(tag) is { } catalogued)
            {
                yield return catalogued;
                continue;
            }

            var colon = tag.IndexOf(':');
            var family = colon > 0 ? tag[..colon] : tag;
            yield return new ModelCard(
                new ModelId(tag),
                AiProvider.Ollama,
                family,
                tag,
                Where.Device,
                Abilities.Text | Abilities.Tools,
                Tier.Balanced);
        }
    }
}
