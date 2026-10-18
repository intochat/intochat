using Microsoft.Extensions.DependencyInjection.Extensions;

namespace IntoChat.OS.AI.Media;

// Host-only transport; no SDK types cross the neuron boundary.
public interface ISpeechSynthesisTransport
{
    Task<bool> IsAvailableAsync(CancellationToken cancellationToken = default);
    string Model { get; }
    Task<MediaPayload> Synthesize(SpeechSynthesisRequest request, CancellationToken cancellationToken);
}
