using DigitalBrain.Framework;
using DigitalBrain.Framework.Parts;
using DigitalBrain.Microsoft.Windows;
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
// the part key is wrapped there, and after a silo restart the state is read back through it.
[System.Runtime.Versioning.SupportedOSPlatform("windows")]
public sealed class TpmRestartFacts
{
    [Fact(Timeout = 180_000)]
    public async Task AStateWrittenUnderTpmCustodySurvivesARestart()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows() && TpmMasterKeyCustody.IsAvailable(), "No TPM (Microsoft Platform Crypto Provider) on this machine.");
        var ct = TestContext.Current.CancellationToken;
        // Test-only key names, so the fact never touches this PC's real digitalbrain-{account} or machine keys.
        var prefix = $"digitalbrain-test-{Guid.NewGuid():N}-";
        try
        {
            var scope = BrainScope.Create(ModuleTest.DefaultAccount, ModuleTest.DefaultAccount).Id;
            await using var brain = await ModuleTest.Create().WithPartLog()
                .WithConfiguration(DigitalBrainNames.CustodyConfigurationKey, nameof(MasterKeyCustodyKind.Tpm))
                .WithModule<WindowsModule>()
                // After the Windows module's own TPM registration, which names the real keys.
                .ConfigureSilo(silo =>
                {
                    var services = silo.Services;
                    services.RemoveAll<IMasterKeyCustody>();
                    services.RemoveAll<IMachineSigningKey>();
                    services.AddSingleton<IMasterKeyCustody>(new TpmMasterKeyCustody(prefix));
                    // MachineIdentity disposes its key with the silo, so each silo opens its own handle to the same TPM key.
                    services.AddSingleton<IMachineSigningKey>(_ => new TpmMachineSigningKey($"{prefix}machine"));
                })
                .StartAsync(ct);
            Assert.IsType<TpmMasterKeyCustody>(brain.SiloServices.GetRequiredService<IMasterKeyCustody>());
            var counter = $"{scope}/apps/counter/tpm";
            for (var i = 0; i < 3; i++) { await brain.Get<ITpmCounter>(counter).Increment(); }

            await brain.RestartSiloAsync(ct);

            Assert.Equal(3, await brain.Get<ITpmCounter>(counter).Read());
        }
        finally
        {
            TpmMasterKeyCustody.Delete(ModuleTest.DefaultAccount, prefix);
            using var machineKey = new TpmMachineSigningKey($"{prefix}machine");
            machineKey.Delete();
        }
    }
}
