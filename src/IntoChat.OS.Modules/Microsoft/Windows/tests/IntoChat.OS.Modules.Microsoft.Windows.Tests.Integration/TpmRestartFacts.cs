using IntoChat.OS.Contracts.Configuration;
using IntoChat.OS.Framework.Parts;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace IntoChat.OS.Modules.Microsoft.Windows.Tests.Integration;

[DigitalBrain.Contracts.NeuronAlias("test.tpm-counter"), DigitalBrain.Contracts.DefaultNeuronType(TpmCounterNeuron.Type)]
public interface ITpmCounter { Task<int> Increment(); Task<int> Read(); }

// A plain neuron (06-08): its state is written to the brain's log under the custody the fact selects.
[DigitalBrain.Neuron(Type)]
public sealed class TpmCounterNeuron : DigitalBrain.Neuron<TpmCounterState>, ITpmCounter
{
    public const string Type = "test.tpm-counter";

    public async Task<int> Increment()
    {
        await Save(new TpmCounterState { Value = State.Value + 1 }, new TpmCounted(State.Value + 1));
        return State.Value;
    }

    public Task<int> Read() => Task.FromResult(State.Value);
}

public sealed class TpmCounterState { public int Value { get; set; } }

[DigitalBrain.Signal("test.tpm-counted")]
public sealed record TpmCounted(int Value) : DigitalBrain.Signal;

[DigitalBrain.Module("test.tpm-counter")]
public sealed class TpmCounterModule : DigitalBrain.Module
{
    public static DigitalBrain.ModuleManifest Manifest { get; } = new(Provides: [], Requires: [], Needs: [], Settings: [], Secrets: []);
}

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
                .WithNeuronModule<TpmCounterModule>()
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
