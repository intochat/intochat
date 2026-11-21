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
public sealed partial class DevicesJson : JsonSerializerContext;
