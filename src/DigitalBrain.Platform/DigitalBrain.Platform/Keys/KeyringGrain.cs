using DigitalBrain.Framework;

namespace DigitalBrain.Platform.Keys;

// Wrapped part keys of one brain, in the trust cluster: ciphertext only, so holding it teaches us nothing.
internal interface IKeyringGrain : IGrainWithStringKey
{
    Task<byte[]?> Get(string partId);

    Task<bool> TryAdd(string partId, byte[] wrapped);
}

[GrainType("platform.keyring")]
internal sealed class KeyringGrain(
    [PersistentState("keyring", DigitalBrainNames.PlatformGrainStorage)] IPersistentState<KeyringState> store) : Grain, IKeyringGrain
{
    public Task<byte[]?> Get(string partId) => Task.FromResult(store.State.Keys.GetValueOrDefault(partId));

    public async Task<bool> TryAdd(string partId, byte[] wrapped)
    {
        if (store.State.Keys.ContainsKey(partId)) { return false; }
        store.State.Keys[partId] = wrapped;
        await store.WriteStateAsync();
        return true;
    }
}

[GenerateSerializer, Alias("platform.keyring-state")]
internal sealed class KeyringState
{
    [Id(0)] public Dictionary<string, byte[]> Keys { get; set; } = new(StringComparer.Ordinal);
}
