using System.Text.Json.Serialization;

namespace IntoChat.OS.Modules.Devices;

[JsonSerializable(typeof(RuntimeId))]
[JsonSerializable(typeof(InstallProgress))]
[JsonSerializable(typeof(Install))]
[JsonSerializable(typeof(InstallChanged))]
[JsonSerializable(typeof(Hardware))]
[JsonSerializable(typeof(RuntimeStatus))]
[JsonSerializable(typeof(DeviceModel))]
[JsonSerializable(typeof(DeviceView))]
[JsonSerializable(typeof(EnrolledDevice))]
[JsonSerializable(typeof(EnrolledDevice[]))]
[JsonSerializable(typeof(EnrollmentOutcome))]
[JsonSerializable(typeof(EnrollmentResult))]
[JsonSerializable(typeof(ComputeConsentMode))]
[JsonSerializable(typeof(ComputeNodeStatus))]
[JsonSerializable(typeof(ComputeExecutionTarget))]
[JsonSerializable(typeof(ComputeCapabilityAdvertisement))]
[JsonSerializable(typeof(ConnectedComputeNode))]
[JsonSerializable(typeof(ConnectedComputeNode[]))]
[JsonSerializable(typeof(ComputeRouteDecision))]
[JsonSerializable(typeof(ConnectedSandboxRequest))]
[JsonSerializable(typeof(ConnectedSandboxResponse))]
public sealed partial class DevicesJson : JsonSerializerContext;
