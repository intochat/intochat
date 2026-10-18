using System.Security.Cryptography;
using DigitalBrain.Authority.Keys;
using DigitalBrain.Authority.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Orleans.Serialization;

namespace DigitalBrain.Authority.Tests.Integration.Storage;

public sealed class BrainLogsFacts
{
    [Fact]
    public async Task TwoSetsOfLogsInOneDirectoryOwnDisjointBrains()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await TestBrain.Create().StartAsync(ct);
        var serializer = brain.Services.GetRequiredService<Serializer>();
        var options = Options.Create(new BrainStoreOptions { Directory = Directory.CreateTempSubdirectory("brain-logs-").FullName });
        var keys = new BrainStorageKeys(new ConfiguredMasterKeyCustody(RandomNumberGenerator.GetBytes(32)), options);
        await using var siloA = new BrainLogs(keys, serializer, options);
        await using var siloB = new BrainLogs(keys, serializer, options);

        await siloA.For("alice").WaitAsync(ct);
        await siloB.For("bob").WaitAsync(ct);

        await Assert.ThrowsAsync<BrainNotOwnedException>(() => siloB.For("alice"));
        await Assert.ThrowsAsync<BrainNotOwnedException>(() => siloA.For("bob"));
    }
}
