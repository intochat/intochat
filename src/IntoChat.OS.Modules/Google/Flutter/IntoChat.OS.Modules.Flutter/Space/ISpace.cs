using Orleans.Concurrency;

namespace IntoChat.OS.Modules.Flutter.Space;

[Alias("ui.space"), Orleans.Metadata.DefaultGrainType("ui.space")]
public interface ISpace : INeuron
{
    [OneWay, AlwaysInterleave] Task Rebuild();
    [OneWay, AlwaysInterleave] Task RefreshApp(string key);
    [OneWay, AlwaysInterleave] Task Recompose();
    Task<IntoChat.OS.Modules.Flutter.Person.PersonState> ReadProfile(string key);
    Task<IntoChat.OS.Modules.Flutter.Card.CardState> ReadPost(string key);
    Task<IntoChat.OS.Modules.Flutter.Collection.CollectionState> ReadPosts(string key);
}
