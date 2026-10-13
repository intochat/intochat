using Orleans.Concurrency;

namespace DigitalBrain.Flutter.Space;

[Alias("ui.space"), Orleans.Metadata.DefaultGrainType("ui.space")]
public interface ISpace : IGrainWithStringKey
{
    [OneWay] Task Rebuild();
}
