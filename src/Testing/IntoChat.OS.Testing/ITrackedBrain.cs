namespace IntoChat.OS.Testing;

public interface ITrackedBrain
{
    int BufferCapacity { get; }
    TestExecutionOptions Execution { get; }
    void Track(IAsyncDisposable resource);
}
