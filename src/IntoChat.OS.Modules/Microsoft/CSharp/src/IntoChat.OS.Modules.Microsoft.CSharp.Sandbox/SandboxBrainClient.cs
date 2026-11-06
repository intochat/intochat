using DigitalBrain.Client;

namespace IntoChat.OS.Modules.Microsoft.CSharp.Sandbox;

public static class SandboxBrainClient
{
    public static Task<DigitalBrainConnection> ConnectAsync(string[] args, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(args);
        cancellationToken.ThrowIfCancellationRequested();
        return AmbientSession.Value is { } scope
            ? Task.FromResult(new DigitalBrainConnection(scope.Session, scope.Behavior, scope.Settings, scope.Stopping))
            : throw new InvalidOperationException("Behaviors run inside a brain's sandbox session; the brain compiles and loads them there.");
    }
}
