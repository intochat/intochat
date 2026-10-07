using Microsoft.Extensions.AI;

namespace DigitalBrain.AI.Tools;

public interface INativeToolContributor
{
    string Name { get; }
    AIFunction Create(IServiceProvider services);
}
