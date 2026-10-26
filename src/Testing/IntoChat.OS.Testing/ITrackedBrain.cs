namespace IntoChat.OS.Testing;

public interface ITrackedBrain
{
    // The signals an observation buffers when no capacity is configured.
    const int DefaultBufferCapacity = 256;

    int BufferCapacity { get; }
    TestExecutionOptions Execution { get; }
    void Track(IAsyncDisposable resource);
}
