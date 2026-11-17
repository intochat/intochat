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
    // The brain client: a typed proxy for the neuron at key (brain-relative) behind TContract. What code may reach is its needs,
    // inferred from these calls and approved by a person; the kernel refuses anything else.
    TContract Get<TContract>(string key) where TContract : class;
    TContract Slot<TContract>(string? slot = null) where TContract : class;
    Task PublishAsync<TSignal>(TSignal signal, CancellationToken cancellationToken = default) where TSignal : DigitalBrain.Signal;
    Task LogAsync(string message, CancellationToken cancellationToken = default);
}
