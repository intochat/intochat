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
public sealed partial class DevicesJson : JsonSerializerContext;
