using IntoChat.OS.Contracts.Configuration;
using IntoChat.OS.Framework;
using IntoChat.OS.Framework.Parts;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace IntoChat.OS.Modules.Microsoft.Windows.Tests.Integration;

public interface ITpmCounter : INeuron { Task<int> Increment(); Task<int> Read(); }

[GrainType("test.tpm-counter")]
public sealed class TpmCounterNeuron([PersistentState("counter", IntoChatOSNames.ClusterGrainStorage)] IPersistentState<TpmCounterState> state) : Neuron, ITpmCounter
{
    public async Task<int> Increment() { state.State.Value++; await state.WriteStateAsync(); return state.State.Value; }

    public Task<int> Read() => Task.FromResult(state.State.Value);
}

[GenerateSerializer, Alias("test.tpm-counter.state")]
public sealed class TpmCounterState { [Id(0)] public int Value { get; set; } }

// The local brain key is wrapped by the TPM and state is recovered after restart.
[System.Runtime.Versioning.SupportedOSPlatform("windows")]
public sealed class TpmRestartFacts
{
    [Trait("host", "own")]
    [Fact]
    public async Task AStateWrittenUnderTpmCustodySurvivesARestart()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows() && TpmMasterKeyCustody.IsAvailable(), "No TPM (Microsoft Platform Crypto Provider) on this machine.");
        var ct = TestContext.Current.CancellationToken;
        // Test-only key names, so the fact never touches this PC's real intochat-os-{account} or machine keys.
        var prefix = $"intochat-os-test-{Guid.NewGuid():N}-";
        try
        {
            await using var brain = await TestBrain.Create()
                .WithConfiguration(IntoChatOSNames.CustodyConfigurationKey, nameof(MasterKeyCustodyKind.Tpm))
                .WithModule<WindowsModule>()
                // The module's TPM custody and signing key under test-only names, registered after the module's own.
                .ConfigureServices(services =>
                {
                    services.RemoveAll<IMasterKeyCustody>();
                    services.AddSingleton<IMasterKeyCustody>(new TpmMasterKeyCustody(prefix));
                })
                .StartAsync(ct);
            Assert.IsType<TpmMasterKeyCustody>(brain.Services.GetRequiredService<IMasterKeyCustody>());
            var counter = brain.Owner.Key("apps/counter/tpm");
            for (var i = 0; i < 3; i++) { await brain.Get<ITpmCounter>(counter).Increment(); }

            await brain.RestartAsync(ct);

            Assert.Equal(3, await brain.Get<ITpmCounter>(counter).Read());
        }
        finally
        {
            TpmMasterKeyCustody.Delete("owner", prefix);
        }
    }
}
