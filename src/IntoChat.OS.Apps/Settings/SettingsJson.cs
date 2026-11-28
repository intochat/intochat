using System.Text.Json;
using System.Text.Json.Serialization;

namespace IntoChat.OS.Apps.Settings;

[JsonSourceGenerationOptions(JsonSerializerDefaults.Web)]
[JsonSerializable(typeof(SettingsMintFriendCode))]
[JsonSerializable(typeof(SettingsRedeemFriendCode))]
[JsonSerializable(typeof(SettingsUnfriend))]
[JsonSerializable(typeof(SettingsLookupHandle))]
[JsonSerializable(typeof(SettingsSetFriendPermission))]
internal sealed partial class SettingsJson : JsonSerializerContext;
