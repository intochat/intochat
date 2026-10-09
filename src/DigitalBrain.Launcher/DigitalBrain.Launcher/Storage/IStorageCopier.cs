namespace DigitalBrain.Launcher.Storage;

public interface IStorageCopier
{
    Task CopyContainerAsync(string source, string destination, CancellationToken ct);
}
