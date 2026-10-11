using System.Collections.Concurrent;
using DigitalBrain.Microsoft.CSharp.Assemblies;
using DigitalBrain.Microsoft.CSharp.Catalog;
using DigitalBrain.Microsoft.CSharp.Runtime;
using Microsoft.Extensions.DependencyInjection;

namespace DigitalBrain.Modules.Microsoft.CSharp.Tests;

public sealed class AssignCachingFacts
{
    private const string Brain = "acc-brain";
    private const string Owner = "acc-brain/apps/x";

    private static Dictionary<string, string> Program(string second)
        => new(StringComparer.Ordinal) { ["synapses/a.cs"] = RuntimeBrain.Idle, ["synapses/b.cs"] = second };

    [Fact]
    public async Task AssigningAProgramAgainWithOneChangedSynapseCompilesOnlyThatSynapse()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await RuntimeBrain.StartAsync(new FakeSessionHost(), ct);
        var programs = RuntimeBrain.Programs(brain, Brain);
        var compiler = brain.Services.GetRequiredService<CSharpScriptCheck>();
        var first = await programs.Assign(Owner, Program(RuntimeBrain.Idle + "// one\n"), []);
        var before = compiler.Emits;

        var second = await programs.Assign(Owner, Program(RuntimeBrain.Idle + "// two\n"), []);

        Assert.Equal(1, compiler.Emits - before);
        Assert.NotEqual(first.Hash, second.Hash);
        var same = await programs.Assign(Owner, Program(RuntimeBrain.Idle + "// two\n"), []);
        Assert.Equal(1, compiler.Emits - before);
        Assert.Equal(second.Hash, same.Hash);
    }

    [Fact]
    public async Task AnAssemblyWhoseBlobExistsIsNotUploadedAgain()
    {
        var ct = TestContext.Current.CancellationToken;
        var store = new RecordingAssemblies();
        await using var brain = await RuntimeBrain.StartAsync(new FakeSessionHost(), ct, assemblies: store);
        var programs = RuntimeBrain.Programs(brain, Brain);
        await programs.Assign(Owner, Program(RuntimeBrain.Idle + "// one\n"), []);
        var uploaded = store.Puts.Count;

        await programs.Assign(Owner, Program(RuntimeBrain.Idle + "// two\n"), []);

        Assert.Equal(uploaded + 1, store.Puts.Count);
        Assert.Equal(store.Puts.Count, store.Puts.Distinct().Count());
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
