using DigitalBrain;

namespace IntoChat.Cloud.Circle;

// D-15: the OS-level circle module API. Joining a circle creates a connection; the Settings flow and the Phase 11 CLI
// both call this one contract. Status types carry no tokens or keys (T-10-17).
public interface ICircleConnection
{
    // Accepted immediately; progress arrives as JoinProgressed, then JoinSucceeded or JoinFailed.
    Task Join(JoinRequest request);

    Task Cancel();

    // Publishes LeaveProgressed then LeftCircle.
    Task Leave();

    // Asks the server again whether a pending account was approved.
    Task CheckAgain();

    Task<CircleConnectionStatus> Status();

    Task SetBackup(string brainId, bool enabled);

    Task PauseBackups(bool paused);

    Task BackUpNow();
}

public sealed record JoinRequest(string Code, string? ServerAddress);

public enum CircleConnectionState
{
    NotJoined,
    Joining,
    PendingAccount,
    Connected,
    Connecting,
    Offline,
    Unavailable,
    Disconnected,
    Problem,
}

public enum JoinStep
{
    Preparing,
    Redeeming,
    Connecting,
    Done,
}

// Expired also covers a code issued for another machine (the UI shows the same copy).
public enum JoinFailure
{
    Rejected,
    Expired,
    RateLimited,
    Unreachable,
    KeyCustody,
    ServerUnverified,
    Timeout,
}

public static class JoinSteps
{
    public static double Progress(JoinStep step) => step switch
    {
        JoinStep.Preparing => 0.1,
        JoinStep.Redeeming => 0.5,
        JoinStep.Connecting => 0.9,
        JoinStep.Done => 1.0,
        _ => throw new ArgumentOutOfRangeException(nameof(step), step, null),
    };
}

public enum SyncState
{
    Off,
    UpToDate,
    Behind,
    Pushing,
    Waiting,
    Error,
    Resync,
    Paused,
}

public enum SyncError
{
    Unreachable,
    Rejected,
    Conflict,
    Storage,
    Unknown,
}

public sealed record BrainSyncStatus(
    string BrainId,
    string BrainName,
    bool BackedUp,
    SyncState State,
    long LastPushedSignal,
    long LastSignal,
    DateTimeOffset? LastAckedAt,
    SyncError? Error,
    int? RetryInSeconds);

// MachineFingerprint is MachineProof.Fingerprint of the machine SPKI.
public sealed record CircleConnectionStatus(
    CircleConnectionState State,
    string? CircleName,
    string? Role,
    DateTimeOffset? JoinedAt,
    DateTimeOffset? LastContactedAt,
    string MachineName,
    string MachineFingerprint,
    ComputeDelegateStatus Compute,
    IReadOnlyList<BrainSyncStatus> Brains);

public sealed record JoinProgressed(JoinStep Step, double Progress) : Signal;

public sealed record JoinSucceeded(string CircleName, string Role, bool PendingAccount) : Signal;

public sealed record JoinFailed(JoinFailure Failure) : Signal;

public sealed record LeaveProgressed(double Progress) : Signal;

public sealed record LeftCircle(bool ServerNotified) : Signal;
