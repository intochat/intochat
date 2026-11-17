namespace IntoChat.Sdk;

/// <summary>Brain surface exposed inside sandboxed apps and deterministic verification harnesses.</summary>
public interface ISandboxBrain
{
    string BrainId { get; }
    string AppKey { get; }
    INotifications Notifications { get; }
    IAi Ai { get; }
    ITables Tables { get; }
    IFolder Folder(string slot = "folder");
    TContract Slot<TContract>(string? slot = null) where TContract : class;
    Task PublishAsync<TSignal>(TSignal signal, CancellationToken cancellationToken = default) where TSignal : DigitalBrain.Signal;
    Task LogAsync(string message, CancellationToken cancellationToken = default);
}
