using DigitalBrain.Client.Runtime;

namespace DigitalBrain.Client;

public static class DigitalBrainClient
{
    // A behavior connects to the session the sandbox host loaded it into; there is no other way in.
    // args stays in the signature every behavior already calls; the session supplies everything.
    public static Task<DigitalBrainConnection> ConnectAsync(string[] args, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(args);
        cancellationToken.ThrowIfCancellationRequested();
        return AmbientSession.Value is { } scope
            ? Task.FromResult(new DigitalBrainConnection(scope.Session, scope.Behavior, scope.Settings, scope.Stopping, scope.Trace, scope.AssignmentHash, scope.AssignmentRevision))
            : throw new InvalidOperationException("Behaviors run inside a brain's sandbox session; the brain compiles and loads them there.");
    }
}
