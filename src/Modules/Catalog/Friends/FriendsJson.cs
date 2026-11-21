using System.Text.Json;
using System.Text.Json.Serialization;

namespace IntoChat.OS.Modules.Apps;

[JsonSourceGenerationOptions(JsonSerializerDefaults.Web)]
[JsonSerializable(typeof(FriendsRecord))]
[JsonSerializable(typeof(PendingFriendCode))]
[JsonSerializable(typeof(Friend))]
[JsonSerializable(typeof(Friend[]))]
[JsonSerializable(typeof(GroupInvite))]
[JsonSerializable(typeof(GroupInvite[]))]
internal sealed partial class FriendsJson : JsonSerializerContext;
