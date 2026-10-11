using DigitalBrain.Framework;
using DigitalBrain.Framework.Parts;
using DigitalBrain.Microsoft.Windows;
using DigitalBrain.Platform.Mesh;
using DigitalBrain.Platform.Parts;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace DigitalBrain.Modules.Microsoft.Windows.Tests;

public interface ITpmCounter : INeuron { Task<int> Increment(); Task<int> Read(); }

[GrainType("test.tpm-counter")]
public sealed class TpmCounterNeuron([PersistentState("counter", DigitalBrainNames.ClusterGrainStorage)] IPersistentState<TpmCounterState> state) : Neuron, ITpmCounter
{
    public async Task<int> Increment() { state.State.Value++; await state.WriteStateAsync(); return state.State.Value; }

    public Task<int> Read() => Task.FromResult(state.State.Value);
}

[GenerateSerializer, Alias("test.tpm-counter.state")]
public sealed class TpmCounterState { [Id(0)] public int Value { get; set; } }

// R78 step 4, scaled to one single-machine owner: the Windows module puts custody and the signing key in the TPM,
// the part key is wrapped there, and after a restart the state is read back through it.
[System.Runtime.Versioning.SupportedOSPlatform("windows")]
public sealed class TpmRestartFacts
{
    [Fact]
    public async Task AStateWrittenUnderTpmCustodySurvivesARestart()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows() && TpmMasterKeyCustody.IsAvailable(), "No TPM (Microsoft Platform Crypto Provider) on this machine.");
        var ct = TestContext.Current.CancellationToken;
        // Test-only key names, so the fact never touches this PC's real digitalbrain-{account} or machine keys.
        var prefix = $"digitalbrain-test-{Guid.NewGuid():N}-";
        try
        {
            await using var brain = await TestBrain.Create().WithConfiguration($"{PartsOptions.Key}:{nameof(PartsOptions.Role)}", nameof(MachineRole.Own))
                .WithConfiguration($"{MeshOptions.Key}:{nameof(MeshOptions.SeedNodes)}", "8")
                .WithConfiguration($"{MeshOptions.Key}:{nameof(MeshOptions.DataShards)}", "4")
                .WithConfiguration($"{MeshOptions.Key}:{nameof(MeshOptions.ParityShards)}", "2")
                .WithConfiguration($"{MeshOptions.Key}:{nameof(MeshOptions.ManifestReplicas)}", "2")
                .WithConfiguration(DigitalBrainNames.CustodyConfigurationKey, nameof(MasterKeyCustodyKind.Tpm))
                .WithModule<WindowsModule>()
                // The module's TPM custody and signing key under test-only names, registered after the module's own.
                .ConfigureServices(services =>
                {
                    services.RemoveAll<IMasterKeyCustody>();
                    services.RemoveAll<IMachineSigningKey>();
                    services.AddSingleton<IMasterKeyCustody>(new TpmMasterKeyCustody(prefix));
                    // MachineIdentity disposes its key with the host, so each host opens its own handle to the same TPM key.
                    services.AddSingleton<IMachineSigningKey>(_ => new TpmMachineSigningKey($"{prefix}machine"));
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
            using var machineKey = new TpmMachineSigningKey($"{prefix}machine");
            machineKey.Delete();
        }
    }
}
