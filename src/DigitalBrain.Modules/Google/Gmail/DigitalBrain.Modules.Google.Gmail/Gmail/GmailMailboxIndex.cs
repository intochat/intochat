using DigitalBrain.Framework;
using Orleans.Runtime;
namespace DigitalBrain.Google.Gmail;

[Alias("gmail.mailbox-index"), Orleans.Metadata.DefaultGrainType("gmail.mailbox-index")]
internal interface IGmailMailboxIndex : IGrainWithStringKey
{
    Task Add(string connectionKey);
    Task Remove(string connectionKey);
    Task<string[]> Read();
}
[GenerateSerializer, Alias("gmail.mailbox-index-state")]
internal sealed record GmailMailboxIndexState { [Id(0)] public string[] Connections { get; init; } = []; }
[GrainType("gmail.mailbox-index")]
internal sealed class GmailMailboxIndex([PersistentState("gmail-mailbox-index", DigitalBrainNames.ClusterGrainStorage)] IPersistentState<GmailMailboxIndexState> store) : Grain, IGmailMailboxIndex
{
    public Task<string[]> Read() => Task.FromResult(store.State.Connections);
    public async Task Add(string connectionKey) { store.State = store.State with { Connections = store.State.Connections.Append(connectionKey).Distinct().ToArray() }; await store.WriteStateAsync(); }
    public async Task Remove(string connectionKey) { store.State = store.State with { Connections = store.State.Connections.Where(x => x != connectionKey).ToArray() }; await store.WriteStateAsync(); }
}
