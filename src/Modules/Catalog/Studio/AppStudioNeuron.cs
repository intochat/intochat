using DigitalBrain.Neurons;
using IntoChat.OS;
using IntoChat.OS.Connections;
using IntoChat.OS.Contracts.Configuration;
using IntoChat.OS.Framework;
using IntoChat.OS.Runtime;
using Microsoft.Extensions.DependencyInjection;

namespace IntoChat.OS.Modules.Apps;

// The Creator, addressable: one neuron per brain wrapping the Author and Builder. Build waits for
// the background Builder to land, so a single routed call answers with the finished app.
// A plain neuron (06-08).
[DigitalBrain.Neurons.Neuron("apps.studio")]
internal sealed class AppStudioNeuron(Creator creator, INeuronProxies proxies) : DigitalBrain.Neurons.Neuron, IAppStudio
{
    private static readonly TimeSpan BuildTimeout = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan Poll = TimeSpan.FromMilliseconds(500);
    private string Scope => Id.Brain.Value;

    public Task<AppSnapshot> Author(string request, CancellationToken ct)
        => creator.Author(proxies, Scope, null, request, ct);

    public async Task<AppSnapshot> Build(string request, CancellationToken ct)
    {
        var authored = await Author(request, ct);
        var appRef = IntoChat.OS.Primitives.AppRef.Of(Id.Brain, authored.Name);
        await creator.Implement(proxies, appRef.Key, null, ct);
        var app = Get<IApp>(appRef.Neuron);
        var deadline = Context.Time.GetUtcNow() + BuildTimeout;
        while (Context.Time.GetUtcNow() < deadline)
        {
            ct.ThrowIfCancellationRequested();
            var snapshot = await AppBuildStatus.Of(await app.Read(), app, Get<Flows.IFlows>(Flows.IFlows.Key(Scope)), ct);
            if (snapshot.Status is AppStatus.Live or AppStatus.Failed) { return snapshot; }
            await Task.Delay(Poll, ct);
        }
        return await app.Read();
    }
}
