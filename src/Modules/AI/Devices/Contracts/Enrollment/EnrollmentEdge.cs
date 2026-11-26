using System.Text.Json;
using System.Text.Json.Serialization;

namespace IntoChat.OS.Modules.Devices;

// Device enrollment on the install's edge, for the cloud relay that runs in its own cluster (owner decision D4: the OS owns
// enrollment). Every request carries "Authorization: Service <assertion>" (DigitalBrain ServiceAssertion) signed by a trusted
// issuer listed with Services, naming the operation below and binding the exact request. The edge acts as the Devices module
// in the brain the code or the route names, never as a person.
public static class EnrollmentEdge
{
    // POST, body RedeemRequest: redeems a code the brain owner minted; the code itself names the brain.
    public const string Redeem = "/devices/enrollment/redeem";

    public const string RedeemOperation = "devices.enrollment.redeem";

    // GET: the brain's enrolled devices (EnrolledDevice[]).
    public const string List = "/brains/{brainId}/devices/enrollment";

    public const string ListOperation = "devices.enrollment.list";

    // GET ?machine=&thumbprint=: whether the brain still enrolls that machine with that key (LiveResponse).
    public const string Live = "/brains/{brainId}/devices/enrollment/live";

    public const string LiveOperation = "devices.enrollment.live";

    public const int MaxBodyBytes = 8 * 1024;

    public static string ListFor(string brainId) => "/brains/" + Uri.EscapeDataString(brainId) + "/devices/enrollment";

    public static string LiveFor(string brainId, string machineId, string thumbprint)
        => ListFor(brainId) + "/live?machine=" + Uri.EscapeDataString(machineId) + "&thumbprint=" + Uri.EscapeDataString(thumbprint);
}

public sealed record RedeemRequest(string Code, string MachineId, string KeyThumbprint, string MachineName);

// Result is "accepted", "repeat", "expired" or "rejected" (EnrollmentResult, lower case).
public sealed record RedeemResponse(string Result, string Brain, string Account, string DeviceName)
{
    public static RedeemResponse From(EnrollmentOutcome outcome)
    {
        ArgumentNullException.ThrowIfNull(outcome);
        return new(outcome.Result.ToString().ToLowerInvariant(), outcome.Brain, outcome.Account, outcome.DeviceName);
    }

    public EnrollmentOutcome ToOutcome()
        => Enum.TryParse<EnrollmentResult>(Result, ignoreCase: true, out var result) && Enum.IsDefined(result)
            ? new EnrollmentOutcome(result, Brain, Account, DeviceName)
            : EnrollmentOutcome.Rejected;
}

public sealed record LiveResponse(bool Live);

[JsonSourceGenerationOptions(JsonSerializerDefaults.Web)]
[JsonSerializable(typeof(RedeemRequest))]
[JsonSerializable(typeof(RedeemResponse))]
[JsonSerializable(typeof(LiveResponse))]
[JsonSerializable(typeof(EnrolledDevice[]))]
public sealed partial class EnrollmentEdgeJson : JsonSerializerContext;
