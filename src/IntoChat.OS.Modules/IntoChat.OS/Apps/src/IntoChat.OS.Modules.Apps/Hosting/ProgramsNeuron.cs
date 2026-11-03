using System.Collections.Concurrent;
using DigitalBrain.Apps.Hosting;

namespace IntoChat.OS.Modules.Apps;

[DigitalBrain.Neuron("digitalbrain.programs")]
internal sealed class ProgramsNeuron(IAppHost host) : DigitalBrain.Neuron, IPrograms
{
    private readonly ConcurrentDictionary<string, StagedBuffer> _staged = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, string> _hashes = new(StringComparer.Ordinal);

    public Task<ProgramView> Assign(string owner, Dictionary<string, string> sources, Dictionary<string, string> settings)
        => throw new NotSupportedException("Source programs are compiled by the csharp module until 0.13");

    public async Task<ProgramView> AssignPrebuilt(
        string owner,
        Dictionary<string, PrebuiltBehavior> behaviors,
        Dictionary<string, string> settings)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(owner);
        ArgumentNullException.ThrowIfNull(behaviors);
        ArgumentNullException.ThrowIfNull(settings);

        var resolved = new Dictionary<string, PrebuiltBehavior>(behaviors.Count, StringComparer.Ordinal);
        foreach (var (path, behavior) in behaviors)
        {
            resolved[path] = behavior.Assembly.Length == 0 && _staged.TryRemove(behavior.Hash, out var staged)
                ? behavior with { Assembly = staged.ToArray() }
                : behavior;
        }

        var hash = resolved.Count == 0
            ? ""
            : string.Join("+", resolved.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => p.Value.Hash));
        _hashes[owner] = hash;
        await host.Assign(new ProgramAssignment(owner, hash, resolved, settings), CancellationToken.None);
        var views = await host.Describe(owner, CancellationToken.None);
        return new ProgramView(owner, hash, views);
    }

    public Task Stage(string hash, int offset, byte[] chunk, bool last)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(hash);
        ArgumentNullException.ThrowIfNull(chunk);
        var buffer = _staged.GetOrAdd(hash, static _ => new StagedBuffer());
        buffer.Write(offset, chunk, last);
        return Task.CompletedTask;
    }

    public async Task Retire(string owner)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(owner);
        _hashes.TryRemove(owner, out _);
        await host.Retire(owner, CancellationToken.None);
    }

    public Task<ProgramView?> Settled(string owner, TimeSpan timeout)
    {
        _ = timeout;
        return Describe(owner);
    }

    public async Task<ProgramView?> Describe(string owner)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(owner);
        var views = await host.Describe(owner, CancellationToken.None);
        if (views.Length == 0)
        {
            return null;
        }
        var hash = _hashes.GetValueOrDefault(owner, views[0].Hash);
        return new ProgramView(owner, hash, views);
    }

    private sealed class StagedBuffer
    {
        private readonly Lock _gate = new();
        private readonly MemoryStream _stream = new();

        public void Write(int offset, byte[] chunk, bool last)
        {
            _ = last;
            lock (_gate)
            {
                if (offset == 0)
                {
                    _stream.SetLength(0);
                }
                _stream.Position = offset;
                _stream.Write(chunk, 0, chunk.Length);
            }
        }

        public byte[] ToArray()
        {
            lock (_gate)
            {
                return _stream.ToArray();
            }
        }
    }
}
