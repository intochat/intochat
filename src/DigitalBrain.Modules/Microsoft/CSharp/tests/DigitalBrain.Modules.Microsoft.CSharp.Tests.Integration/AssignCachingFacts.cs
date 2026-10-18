using System.Collections.Concurrent;
using Azure;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using DigitalBrain.Microsoft.CSharp.Assemblies;
using DigitalBrain.Microsoft.CSharp.Catalog;
using DigitalBrain.Microsoft.CSharp.Runtime;
using Microsoft.Extensions.DependencyInjection;

namespace DigitalBrain.Modules.Microsoft.CSharp.Tests.Integration;

public sealed class AssignCachingFacts
{
    private const string Brain = "acc-brain";
    private const string Owner = "acc-brain/apps/x";

    private static string Unique(string name) => RuntimeBrain.Idle + "// " + name + " " + Guid.NewGuid() + "\n";

    private static Dictionary<string, string> Program(string second)
        => new(StringComparer.Ordinal) { ["synapses/a.cs"] = RuntimeBrain.Idle, ["synapses/b.cs"] = second };

    [Trait("host", "own")]
    [Fact]
    public async Task AssigningAProgramAgainWithOneChangedSynapseCompilesOnlyThatSynapse()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await RuntimeBrain.StartAsync(new FakeSessionHost(), ct);
        var programs = RuntimeBrain.Programs(brain, Brain);
        var compiler = brain.Services.GetRequiredService<CSharpScriptCheck>();
        var one = Unique("one");
        var two = Unique("two");
        var first = await programs.Assign(Owner, Program(one), []);
        var before = compiler.Emits;

        var second = await programs.Assign(Owner, Program(two), []);

        Assert.Equal(1, compiler.Emits - before);
        Assert.NotEqual(first.Hash, second.Hash);
        var same = await programs.Assign(Owner, Program(two), []);
        Assert.Equal(1, compiler.Emits - before);
        Assert.Equal(second.Hash, same.Hash);
    }

    [Trait("host", "own")]
    [Fact]
    public async Task AnAssemblyWhoseBlobExistsIsNotUploadedAgain()
    {
        var ct = TestContext.Current.CancellationToken;
        var store = new RecordingAssemblies();
        await using var brain = await RuntimeBrain.StartAsync(new FakeSessionHost(), ct, assemblies: store);
        var programs = RuntimeBrain.Programs(brain, Brain);
        var one = Unique("one");
        var two = Unique("two");
        await programs.Assign(Owner, Program(one), []);
        var uploaded = store.Puts.Count;

        await programs.Assign(Owner, Program(two), []);

        Assert.Equal(uploaded + 1, store.Puts.Count);
        Assert.Equal(store.Puts.Count, store.Puts.Distinct().Count());
    }

    [Fact]
    public async Task ChecksWithDifferentReferenceFingerprintsDoNotShareACompiledSynapse()
    {
        var files = new Dictionary<string, string> { ["a.cs"] = "Console.WriteLine(1);// " + Guid.NewGuid() + "\n" };
        var one = new CSharpScriptCheck("references-one");
        var sameAsOne = new CSharpScriptCheck("references-one");
        var other = new CSharpScriptCheck("references-two");

        one.Compile(files);
        sameAsOne.Compile(files);
        other.Compile(files);

        Assert.Equal(1, one.Emits);
        Assert.Equal(0, sameAsOne.Emits);
        Assert.Equal(1, other.Emits);
    }

    [Fact]
    public async Task AnAssemblyBlobWithoutItsSymbolsIsUploadedAgain()
    {
        var ct = TestContext.Current.CancellationToken;
        var blobs = new FakeBlobs();
        var store = new BlobBehaviorAssemblies(blobs);
        var behavior = new CSharpScriptCheck().Compile(new Dictionary<string, string> { ["a.cs"] = "Console.WriteLine(1);// " + Guid.NewGuid() + "\n" }).Assemblies["a.cs"];
        blobs.Stored.Add(behavior.Hash);

        Assert.False(await store.Exists(behavior.Hash, ct));
        await store.Put(behavior, ct);

        Assert.True(await store.Exists(behavior.Hash, ct));
        Assert.Equal([behavior.Hash, behavior.Hash + ".pdb"], blobs.Uploads);
    }

    private sealed class FakeBlobs : BlobServiceClient
    {
        public HashSet<string> Stored { get; } = [];
        public List<string> Uploads { get; } = [];
        public override BlobContainerClient GetBlobContainerClient(string blobContainerName) => new FakeContainer(this);
    }

    private sealed class FakeContainer(FakeBlobs blobs) : BlobContainerClient
    {
        public override Task<Response<BlobContainerInfo>> CreateIfNotExistsAsync(PublicAccessType publicAccessType = PublicAccessType.None, IDictionary<string, string>? metadata = null, BlobContainerEncryptionScopeOptions? encryptionScopeOptions = null, CancellationToken cancellationToken = default)
            => Task.FromResult<Response<BlobContainerInfo>>(null!);
        public override BlobClient GetBlobClient(string blobName) => new FakeBlob(blobs, blobName);
    }

    private sealed class FakeBlob(FakeBlobs blobs, string name) : BlobClient
    {
        public override Task<Response<bool>> ExistsAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(Response.FromValue(blobs.Stored.Contains(name), null!));
        public override Task<Response<BlobContentInfo>> UploadAsync(BinaryData content, bool overwrite = false, CancellationToken cancellationToken = default)
        {
            blobs.Stored.Add(name);
            blobs.Uploads.Add(name);
            return Task.FromResult<Response<BlobContentInfo>>(null!);
        }
    }

    private sealed class RecordingAssemblies : IBehaviorAssemblies
    {
        private readonly ConcurrentDictionary<string, byte> _stored = new();
        public ConcurrentQueue<string> Puts { get; } = new();

        public Task Put(CompiledBehavior behavior, CancellationToken cancellationToken)
        {
            Puts.Enqueue(behavior.Hash);
            _stored[behavior.Hash] = 0;
            return Task.CompletedTask;
        }

        public Task<bool> Exists(string hash, CancellationToken cancellationToken) => Task.FromResult(_stored.ContainsKey(hash));

        public Task<Uri> ReadUrl(string name, TimeSpan lifetime, CancellationToken cancellationToken)
            => Task.FromResult(new Uri("memory://behaviors/" + name));
    }
}
