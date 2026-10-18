using IntoChat.OS.Framework;
using Orleans.Runtime;

namespace IntoChat.OS.Microsoft.GitHub;

// Keyed installation/{installationId}: the connection keys a webhook for that installation reaches.
[Alias("github.repository-index"), Orleans.Metadata.DefaultGrainType("github.repository-index")]
internal interface IGitHubRepositoryIndex : IGrainWithStringKey
{
    Task Add(string connectionKey);
    Task Remove(string connectionKey);
    Task<string[]> Read();
}

[GenerateSerializer, Alias("github.repository-index-state")]
[ExportableState]
internal sealed record GitHubRepositoryIndexState { [Id(0)] public string[] Keys { get; init; } = []; }

[GrainType("github.repository-index")]
internal sealed class GitHubRepositoryIndex([PersistentState("github-repository-index", IntoChatOSNames.ClusterGrainStorage)] IPersistentState<GitHubRepositoryIndexState> store) : Grain, IGitHubRepositoryIndex
{
    public Task<string[]> Read() => Task.FromResult(store.State.Keys);
    public async Task Add(string connectionKey) { store.State = store.State with { Keys = store.State.Keys.Append(connectionKey).Distinct().ToArray() }; await store.WriteStateAsync(); }
    public async Task Remove(string connectionKey) { store.State = store.State with { Keys = store.State.Keys.Where(x => x != connectionKey).ToArray() }; await store.WriteStateAsync(); }
}
