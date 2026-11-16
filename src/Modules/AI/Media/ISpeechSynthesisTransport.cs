using Microsoft.Extensions.DependencyInjection.Extensions;

namespace IntoChat.OS.Modules.AI.Media;

// Host-only transport; no SDK types cross the neuron boundary.
internal interface ISpeechSynthesisTransport
{
    Task<bool> IsAvailableAsync(CancellationToken cancellationToken = default);
    string Model { get; }
    Task<MediaPayload> Synthesize(SpeechSynthesisRequest request, CancellationToken cancellationToken);
}
