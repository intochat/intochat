using System.Runtime.CompilerServices;
using DigitalBrain;
using IntoChat.OS.Modules.AI;
using IntoChat.OS.Modules.AI.Models;
using IntoChat.OS.Modules.Devices;
using Microsoft.Extensions.AI;

namespace IntoChat.OS.Tests.Unit.Modules.AI.Models;

public sealed class LocalRuntimeInventoryFacts
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task ProbeOrdersRuntimesSwallowsFailuresAndMergesInstalledBytes()
    {
        const long gemmaBytes = 8_000_000_000L;
        var dynamicCard = new ModelCard(
            new ModelId("custom-local:7b"),
            AiProvider.Ollama,
            "custom-local",
            "custom-local:7b",
            Where.Device,
            Abilities.Text | Abilities.Tools,
            Tier.Balanced);

        var ollama = new StubRuntime(
            RuntimeId.Ollama,
            reachable: true,
            new Uri("http://127.0.0.1:11434"),
            [
                KnownModels.Gemma412B with { Bytes = gemmaBytes },
                dynamicCard,
            ]);
        var broken = new ThrowingRuntime(RuntimeId.FoundryLocal);

        var inventory = new LocalRuntimeInventory([broken, ollama]);

        var ordered = inventory.ActiveRuntimes();
        Assert.Equal([RuntimeId.Ollama, RuntimeId.FoundryLocal], ordered.Select(static r => r.Id).ToArray());

        var snapshot = await inventory.ProbeAsync(Ct);

        Assert.True(snapshot.IsProviderReachable(AiProvider.Ollama, out var ollamaUri));
        Assert.Equal(new Uri("http://127.0.0.1:11434"), ollamaUri);
        Assert.False(snapshot.IsProviderReachable(AiProvider.FoundryLocal, out _));

        Assert.True(snapshot.TryGetInstalled(KnownModels.Gemma412B.Id.Value, out var gemmaHit));
        Assert.Equal(gemmaBytes, gemmaHit.Card.Bytes);
        Assert.Contains(snapshot.Cards, c => c.Id == dynamicCard.Id);
    }

    [Fact]
    public async Task FindInstalledRuntimeMatchesDirectAndCanonicalIds()
    {
        var sherpa = new StubRuntime(
            RuntimeId.SherpaOnnx,
            reachable: true,
            endpoint: null,
            [KnownModels.Kokoro82M]);

        var inventory = new LocalRuntimeInventory([sherpa]);

        var found = await inventory.FindInstalledRuntimeAsync(KnownModels.Kokoro82M.Id, logger: null, Ct);
        Assert.Same(sherpa, found);

        var missing = await inventory.FindInstalledRuntimeAsync(KnownModels.WhisperLargeV3Turbo.Id, logger: null, Ct);
        Assert.Null(missing);
    }

    [Fact]
    public async Task TryCreateDeviceRouteClientReportsExactUnavailableReasons()
    {
        var inventoryWithoutRoute = new LocalRuntimeInventory([], deviceRoute: null);

        var badModel = new ResolvedModel(AiProvider.Device, "not-a-device-ref", null, null, string.Empty, Abilities.Text, null, null);
        var (_, invalidReason) = await inventoryWithoutRoute.TryCreateDeviceRouteClientAsync("ada", badModel, Ct);
        Assert.Equal("invalid-device-id", invalidReason);

        var validModel = new ResolvedModel(AiProvider.Device, "device:pc/gemma4:12b", null, null, string.Empty, Abilities.Text, null, null);
        var (_, noBrainReason) = await inventoryWithoutRoute.TryCreateDeviceRouteClientAsync(null, validModel, Ct);
        Assert.Equal("no-brain", noBrainReason);

        var (_, noRouteReason) = await inventoryWithoutRoute.TryCreateDeviceRouteClientAsync("ada", validModel, Ct);
        Assert.Equal("no-route", noRouteReason);
    }

    private sealed class StubRuntime(
        RuntimeId id,
        bool reachable,
        Uri? endpoint,
        ModelCard[] installed) : ILocalRuntime
    {
        public RuntimeId Id => id;

        public Task<RuntimeStatus> Probe(CancellationToken ct)
            => Task.FromResult(new RuntimeStatus(id, reachable, reachable ? "1.0" : null, endpoint));

        public async IAsyncEnumerable<ModelCard> Catalog([EnumeratorCancellation] CancellationToken ct)
        {
            await Task.CompletedTask;
            foreach (var c in installed) { yield return c; }
        }

        public async IAsyncEnumerable<ModelCard> Installed([EnumeratorCancellation] CancellationToken ct)
        {
            await Task.CompletedTask;
            if (!reachable) { yield break; }
            foreach (var c in installed) { yield return c; }
        }

        public async IAsyncEnumerable<InstallProgress> Install(ModelId model, [EnumeratorCancellation] CancellationToken ct)
        {
            await Task.CompletedTask;
            yield break;
        }

        public Task Remove(ModelId model, CancellationToken ct) => Task.CompletedTask;
        public IChatClient Chat(ModelId model) => throw new NotSupportedException();
    }

    private sealed class ThrowingRuntime(RuntimeId id) : ILocalRuntime
    {
        public RuntimeId Id => id;
        public Task<RuntimeStatus> Probe(CancellationToken ct) => throw new InvalidOperationException("boom");
        public IAsyncEnumerable<ModelCard> Catalog(CancellationToken ct) => throw new NotSupportedException();
        public IAsyncEnumerable<ModelCard> Installed(CancellationToken ct) => throw new NotSupportedException();
        public IAsyncEnumerable<InstallProgress> Install(ModelId model, CancellationToken ct) => throw new NotSupportedException();
        public Task Remove(ModelId model, CancellationToken ct) => Task.CompletedTask;
        public IChatClient Chat(ModelId model) => throw new NotSupportedException();
    }
}
