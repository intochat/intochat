using Orleans.Concurrency;

namespace DigitalBrain.Flutter.Space;

[Alias("ui.space"), Orleans.Metadata.DefaultGrainType("ui.space")]
public interface ISpace : INeuron
{
    [OneWay, AlwaysInterleave] Task Rebuild();
    [OneWay, AlwaysInterleave] Task RefreshApp(string key);
    [OneWay, AlwaysInterleave] Task Recompose();
    Task<DigitalBrain.Flutter.Person.PersonState> ReadProfile(string key);
    Task<DigitalBrain.Flutter.Card.CardState> ReadPost(string key);
    Task<DigitalBrain.Flutter.Collection.CollectionState> ReadPosts(string key);
}
