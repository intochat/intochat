using DigitalBrain.Contracts;


namespace IntoChat.OS.Modules.Flutter.Space;

[NeuronAlias("ui.space"), DefaultNeuronType("ui.space")]
// A brain's one space, keyed {brain}/main (R4-18; clean break from the bare brain key).
public interface ISpace
{
    const string Type = "ui.space";

    static string Key(string brainId) => $"{brainId}/main";

    [DigitalBrain.Contracts.OneWayCall, DigitalBrain.Contracts.Interleaves] Task Rebuild();
    [DigitalBrain.Contracts.OneWayCall, DigitalBrain.Contracts.Interleaves] Task RefreshApp(string key);
    [DigitalBrain.Contracts.OneWayCall, DigitalBrain.Contracts.Interleaves] Task Recompose();
    Task<PersonState> ReadProfile(string key);
    Task<CardState> ReadPost(string key);
    Task<CollectionState> ReadPosts(string key);

    // The brain's own person writes its profile and posts ({brain}/profile, {brain}/posts/{id}); they are the Space's records.
    Task SetProfile(string displayName, string avatarUrl);
    Task Post(string id, string title, string body);
}
