using System.Security.Cryptography;
using DigitalBrain.Platform.Keys;
using DigitalBrain.Platform.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Orleans.Serialization;

namespace DigitalBrain.Platform.Tests.Unit.Storage;

public sealed class BrainRecoveryFacts
{
    [Fact]
    public async Task MissingCheckpointManifestCannotResetAPopulatedBrain()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await TestBrain.Create().StartAsync(ct);
        var directory = Directory.CreateTempSubdirectory("brain-manifest-").FullName;
        var key = RandomNumberGenerator.GetBytes(32);
        var serializer = brain.Services.GetRequiredService<Serializer>();
        using (var log = new BrainLog(directory, "owner", key, serializer))
        {
            await log.Append("g", "s", null, BrainRecordChange.Write, [1], [], ct);
            await log.Checkpoint(ct);
        }
        File.Delete(Path.Combine(directory, "current"));
        Assert.Throws<InvalidDataException>(() => new BrainLog(directory, "owner", key, serializer));
        Assert.False(File.Exists(Path.Combine(directory, "initial.log")));
    }

    [Fact]
    public async Task CheckpointRetainsSignalsWithoutObsoleteStatePayloads()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await TestBrain.Create().StartAsync(ct);
        var directory = Directory.CreateTempSubdirectory("brain-compact-").FullName;
        var key = RandomNumberGenerator.GetBytes(32);
        var serializer = brain.Services.GetRequiredService<Serializer>();
        using (var log = new BrainLog(directory, "owner", key, serializer))
        {
            long? etag = null;
            for (var i = 0; i < 5; i++)
            { etag = await log.Append("g", "s", etag, BrainRecordChange.Write, RandomNumberGenerator.GetBytes(100_000), [new BrainLogFacts.Changed(i.ToString())], ct); }
            await log.Checkpoint(ct);
        }
        Assert.InRange(new FileInfo(Assert.Single(Directory.GetFiles(directory, "*.snapshot"))).Length, 100_000, 150_000);
        using var recovered = new BrainLog(directory, "owner", key, serializer);
        Assert.Equal(100_000, recovered.Read("g", "s")!.Record.Length);
        Assert.Equal(5, (await recovered.ReadSignals(0, 10, null, ct)).Length);
    }

    [Fact]
    public async Task AFailedAppendAcknowledgesNothingAndRefusesFurtherWritesUntilReopened()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await TestBrain.Create().StartAsync(ct);
        var directory = Directory.CreateTempSubdirectory("brain-failure-").FullName;
        var key = RandomNumberGenerator.GetBytes(32);
        var serializer = brain.Services.GetRequiredService<Serializer>();
        using (var log = new BrainLog(directory, "owner", key, serializer))
        {
            await log.Append("g", "s", null, BrainRecordChange.Write, [1], [], ct);
            using (var locked = new FileStream(Path.Combine(directory, "initial.log"), FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                await Assert.ThrowsAsync<IOException>(() => log.Append("g", "s", 1, BrainRecordChange.Write, [2], [new BrainLogFacts.Changed("unacknowledged")], ct));
            }
            await Assert.ThrowsAsync<IOException>(() => log.Append("g", "s", 1, BrainRecordChange.Write, [3], [], ct));
        }
        using var recovered = new BrainLog(directory, "owner", key, serializer);
        Assert.Equal(new byte[] { 1 }, recovered.Read("g", "s")!.Record);
        Assert.Empty(await recovered.ReadSignals(0, 100, null, ct));
    }

    [Fact]
    public async Task AnInterruptedCheckpointKeepsTheLastCommittedGeneration()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await TestBrain.Create().StartAsync(ct);
        var directory = Directory.CreateTempSubdirectory("brain-checkpoint-").FullName;
        var key = RandomNumberGenerator.GetBytes(32);
        var serializer = brain.Services.GetRequiredService<Serializer>();
        using (var log = new BrainLog(directory, "owner", key, serializer))
        {
            await log.Append("g", "s", null, BrainRecordChange.Write, [1], [new BrainLogFacts.Changed("saved")], ct);
            await log.Checkpoint(ct);
        }
        var orphan = Guid.NewGuid().ToString("N");
        await File.WriteAllBytesAsync(Path.Combine(directory, orphan + ".snapshot"), [0], ct);
        await File.WriteAllTextAsync(Path.Combine(directory, "current.tmp"), orphan, ct);
        using var recovered = new BrainLog(directory, "owner", key, serializer);
        Assert.Equal(new byte[] { 1 }, recovered.Read("g", "s")!.Record);
        Assert.Equal("saved", Assert.IsType<BrainLogFacts.Changed>(Assert.Single(await recovered.ReadSignals(0, 10, null, ct)).Signal).Value);
    }

    [Fact]
    public async Task MissingOrWrongCustodyNeverReplacesAnExistingBrainKey()
    {
        var ct = TestContext.Current.CancellationToken;
        var options = Options.Create(new BrainStoreOptions { Directory = Directory.CreateTempSubdirectory("brain-key-").FullName });
        var keys = new BrainStorageKeys(new ConfiguredMasterKeyCustody(RandomNumberGenerator.GetBytes(32)), options);
        var key = await keys.GetOrCreate("alice", ct);
        Assert.NotEqual(key, await keys.GetOrCreate("bob", ct));
        var path = Path.Combine(keys.DirectoryOf("alice"), "brain.key");
        var wrapped = await File.ReadAllBytesAsync(path, ct);
        var wrong = new BrainStorageKeys(new ConfiguredMasterKeyCustody(RandomNumberGenerator.GetBytes(32)), options);
        await Assert.ThrowsAnyAsync<CryptographicException>(() => wrong.GetOrCreate("alice", ct));
        Assert.Equal(wrapped, await File.ReadAllBytesAsync(path, ct));
        await File.WriteAllBytesAsync(Path.Combine(keys.DirectoryOf("alice"), "initial.log"), [0], ct);
        File.Delete(path);
        await Assert.ThrowsAsync<CryptographicException>(() => keys.GetOrCreate("alice", ct));
        Assert.False(File.Exists(path));
    }
}
