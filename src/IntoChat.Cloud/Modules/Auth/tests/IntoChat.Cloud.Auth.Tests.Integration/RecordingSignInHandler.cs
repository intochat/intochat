using System.Collections.Concurrent;
using DigitalBrain.Capabilities.Identity;

namespace IntoChat.Cloud.Auth.Tests;

// The SSO hook the platform runs after every issued session; records who signed in.
public sealed class RecordingSignInHandler : ISignInHandler
{
    private readonly ConcurrentQueue<string> principals = new();

    public IReadOnlyList<string> Principals => [.. principals];

    public int CountFor(string principalId) => principals.Count(principal => principal == principalId);

    public Task SignedIn(Member member, CancellationToken cancellationToken)
    {
        principals.Enqueue(member.PrincipalId);
        return Task.CompletedTask;
    }
}
