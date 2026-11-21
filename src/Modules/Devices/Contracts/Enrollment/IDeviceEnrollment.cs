using System.Security.Cryptography;
using DigitalBrain.Access;
using DigitalBrain.Contracts;
using IntoChat.OS.Runtime;

namespace IntoChat.OS.Modules.Devices;

public sealed record EnrolledDevice(
    string MachineId,
    string Name,
    string KeyThumbprint,
    DateTimeOffset EnrolledAt,
    bool Revoked = false);

public enum EnrollmentResult
{
    Accepted = 0,
    Repeat = 1,
    Expired = 2,
    Rejected = 3,
}

public sealed record EnrollmentOutcome(
    EnrollmentResult Result,
    string Brain = "",
    string Account = "",
    string DeviceName = "")
{
    public static EnrollmentOutcome Rejected { get; } = new(EnrollmentResult.Rejected);
    public static EnrollmentOutcome Expired { get; } = new(EnrollmentResult.Expired);
}

// One enrollment neuron per brain at "{brain}/device-enrollment" (distinct from "{brain}/devices/{machineId}").
[NeuronAlias("devices.enrollment"), DefaultNeuronType("devices.enrollment")]
public interface IDeviceEnrollment
{
    static readonly TimeSpan RepeatWindow = TimeSpan.FromMinutes(5);

    Task<Code> Mint();

    Task<EnrolledDevice[]> List();

    Task Revoke(string machineId);

    [Internal]
    Task<EnrollmentOutcome> Redeem(string codeHash, string machineId, string keyThumbprint, string machineName);

    [Internal]
    Task<bool> IsLive(string machineId, string keyThumbprint);

    static string Key(string brain) => $"{brain}/device-enrollment";
}

// In-process redeem for the cloud enrollment endpoint: parses and hashes the presented code and redeems it in the owning brain.
public interface IDeviceEnrollmentRedeemer
{
    Task<EnrollmentOutcome> Redeem(string code, string machineId, string machineKeyThumbprint, string machineName);
}

public static class DeviceKeys
{
    // Base64url SHA-256 of a SubjectPublicKeyInfo: the machine key thumbprint an enrollment binds.
    public static string Thumbprint(ReadOnlySpan<byte> subjectPublicKeyInfo)
        => Convert.ToBase64String(SHA256.HashData(subjectPublicKeyInfo))
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
}
