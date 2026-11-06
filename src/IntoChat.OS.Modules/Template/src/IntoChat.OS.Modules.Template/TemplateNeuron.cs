using DigitalBrain;
using DigitalBrain.Contracts;
using IntoChat.OS.Modules.Template.Contracts;
using Microsoft.Extensions.Options;

namespace IntoChat.OS.Modules.Template;

[Neuron("template.echo")]
internal sealed class TemplateNeuron(IOptions<TemplateOptions>? options = null) : DigitalBrain.Neuron, ITemplateNeuron
{
    public Task<string> Echo(string message, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(message);
        var prefix = options?.Value.DefaultPrefix ?? "Echo";
        return Task.FromResult($"{prefix}: {message}");
    }
}
