using DigitalBrain.Launcher.Storage;

namespace DigitalBrain.Launcher.Tests.Unit;

internal sealed class FakeStorageCopier : IStorageCopier
{
    public List<(string Source, string Destination)> Copies { get; } = [];
    public Task CopyContainerAsync(string source, string destination, CancellationToken ct) { Copies.Add((source, destination)); return Task.CompletedTask; }
}
