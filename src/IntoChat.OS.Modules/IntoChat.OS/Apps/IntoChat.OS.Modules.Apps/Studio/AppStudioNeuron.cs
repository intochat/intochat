using IntoChat.OS;
using IntoChat.OS.Framework;
using Microsoft.Extensions.DependencyInjection;

namespace IntoChat.OS.Apps;

// The Creator, addressable: one neuron per brain wrapping the Author and Builder. Build waits for
// the background Builder to land, so a single routed call answers with the finished app.
[GrainType("apps.studio")]
internal sealed class AppStudioNeuron(Creator creator) : Neuron, IAppStudio
{
    private static readonly TimeSpan BuildTimeout = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan Poll = TimeSpan.FromMilliseconds(500);
    private string Scope => BrainKey.ScopeOf(this.GetPrimaryKeyString()) ?? this.GetPrimaryKeyString();

    public Task<AppSnapshot> Author(string request, CancellationToken ct)
        => creator.Author(ServiceProvider.GetRequiredService<IIntoChatOS>(), Scope, null, request, ct);

    public async Task<AppSnapshot> Build(string request, CancellationToken ct)
    {
        var authored = await Author(request, ct);
        var key = IApp.Key(Scope, authored.Name);
        await creator.Implement(ServiceProvider.GetRequiredService<IIntoChatOS>(), key, null, ct);
        var app = GrainFactory.GetGrain<IApp>(key);
        var deadline = DateTimeOffset.UtcNow + BuildTimeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            var snapshot = await app.Read();
            if (snapshot.Status is AppStatus.Live or AppStatus.Failed) { return snapshot; }
            await Task.Delay(Poll, ct);
        }
        return await app.Read();
    }
}
