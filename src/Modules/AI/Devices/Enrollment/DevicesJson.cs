using System.Text.Json;
using System.Text.Json.Serialization;

namespace IntoChat.OS.Modules.Devices;

[JsonSourceGenerationOptions(JsonSerializerDefaults.Web)]
[JsonSerializable(typeof(DeviceEnrollmentRecord))]
[JsonSerializable(typeof(DeviceCode))]
[JsonSerializable(typeof(EnrolledDevice))]
[JsonSerializable(typeof(EnrolledDevice[]))]
[JsonSerializable(typeof(EnrollmentOutcome))]
[JsonSerializable(typeof(EnrollmentResult))]
internal sealed partial class DeviceEnrollmentJson : JsonSerializerContext;
