using System.Security.Cryptography;
using DigitalBrain.Platform.Storage;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Serialization;
using Orleans.Storage;

namespace DigitalBrain.Platform.Tests.Unit.Storage;

public sealed class BrainLogFacts
{
    [GenerateSerializer, Alias("test.local-store.changed")]
    public sealed record Changed([property: Id(0)] string Value) : Signal;

    [Fact]
    public async Task StateAndEverySignalSurviveCheckpointAndReopenWithStableCursors()
    {
        await using var brain = await TestBrain.Create().StartAsync(TestContext.Current.CancellationToken);
        var serializer = brain.Services.GetRequiredService<Serializer>();
        var directory = Directory.CreateTempSubdirectory("brain-log").FullName;
        var key = RandomNumberGenerator.GetBytes(32);
        using (var log = new BrainLog(directory, "owner", key, serializer))
        {
            Assert.Equal(1, await log.Append("g", "state", null, BrainRecordChange.Write, [42], [new Changed("one"), new Changed("two")], TestContext.Current.CancellationToken));
            await log.Append("g", "", null, BrainRecordChange.None, [], [new Changed("three")], TestContext.Current.CancellationToken);
            await log.Checkpoint(TestContext.Current.CancellationToken);
        }
        using var reopened = new BrainLog(directory, "owner", key, serializer);
        Assert.Equal("*"u8.ToArray(), reopened.Read("g", "state")!.Record);
        var first = Assert.Single(await reopened.ReadSignals(0, 1, null, TestContext.Current.CancellationToken));
        var second = Assert.Single(await reopened.ReadSignals(first.Sequence, 1, "g", TestContext.Current.CancellationToken));
        var third = Assert.Single(await reopened.ReadSignals(second.Sequence, 1, null, TestContext.Current.CancellationToken));
        Assert.Equal(new long[] { 1, 2, 3 }, new long[] { first.Sequence, second.Sequence, third.Sequence });
        Assert.Equal("two", Assert.IsType<Changed>(second.Signal).Value);
        Assert.Empty(await reopened.ReadSignals(third.Sequence, 10, null, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ARejectedStateWriteDoesNotAppendItsSignals()
    {
        await using var brain = await TestBrain.Create().StartAsync(TestContext.Current.CancellationToken);
        using var log = new BrainLog(Directory.CreateTempSubdirectory("brain-log").FullName, "owner", RandomNumberGenerator.GetBytes(32), brain.Services.GetRequiredService<Serializer>());
        await log.Append("g", "s", null, BrainRecordChange.Write, [1], [], TestContext.Current.CancellationToken);
        await Assert.ThrowsAsync<InconsistentStateException>(() => log.Append("g", "s", null, BrainRecordChange.Write, [2], [new Changed("lost")], TestContext.Current.CancellationToken));
        Assert.Equal(new byte[] { 1 }, log.Read("g", "s")!.Record);
        Assert.Empty(await log.ReadSignals(0, 10, null, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ATornTailRecoversButACorruptCompleteRecordIsRefused()
    {
        await using var brain = await TestBrain.Create().StartAsync(TestContext.Current.CancellationToken);
        var serializer = brain.Services.GetRequiredService<Serializer>();
        var directory = Directory.CreateTempSubdirectory("brain-log").FullName;
        var key = RandomNumberGenerator.GetBytes(32);
        using (var log = new BrainLog(directory, "owner", key, serializer))
        { await log.Append("g", "s", null, BrainRecordChange.Write, [1], [], TestContext.Current.CancellationToken); }
        var journal = Assert.Single(Directory.GetFiles(directory, "*.log"));
        await File.AppendAllBytesAsync(journal, [0, 0], TestContext.Current.CancellationToken);
        using (var recovered = new BrainLog(directory, "owner", key, serializer))
        { Assert.Equal(new byte[] { 1 }, recovered.Read("g", "s")!.Record); }
        var bytes = await File.ReadAllBytesAsync(journal, TestContext.Current.CancellationToken);
        bytes[^1] ^= 1;
        await File.WriteAllBytesAsync(journal, bytes, TestContext.Current.CancellationToken);
        Assert.ThrowsAny<CryptographicException>(() => new BrainLog(directory, "owner", key, serializer));
    }

    [Fact]
    public async Task AnotherWriterAndAnotherKeyCannotOpenTheStore()
    {
        await using var brain = await TestBrain.Create().StartAsync(TestContext.Current.CancellationToken);
        var serializer = brain.Services.GetRequiredService<Serializer>();
        var directory = Directory.CreateTempSubdirectory("brain-log").FullName;
        var key = RandomNumberGenerator.GetBytes(32);
        using (var log = new BrainLog(directory, "owner", key, serializer))
        {
            Assert.Throws<IOException>(() => new BrainLog(directory, "owner", key, serializer));
            await log.Append("private-grain", "s", null, BrainRecordChange.Write, [1], [], TestContext.Current.CancellationToken);
        }
        Assert.ThrowsAny<CryptographicException>(() => new BrainLog(directory, "owner", RandomNumberGenerator.GetBytes(32), serializer));
        var destination = Directory.CreateTempSubdirectory("brain-log-copy").FullName;
        foreach (var file in Directory.GetFiles(directory)) { File.Copy(file, Path.Combine(destination, Path.GetFileName(file))); }
        using var copy = new BrainLog(destination, "owner", key, serializer);
        Assert.NotNull(copy.Read("private-grain", "s"));
    }
}


