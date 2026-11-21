using System.Text.Json;
using System.Text.Json.Serialization;
using IntoChat.OS.Modules.Circles.Signals;

namespace IntoChat.OS.Modules.Circles;

[JsonSourceGenerationOptions(JsonSerializerDefaults.Web)]
[JsonSerializable(typeof(CircleRecord))]
[JsonSerializable(typeof(CircleBinding))]
[JsonSerializable(typeof(CircleState))]
[JsonSerializable(typeof(CircleChatRecord))]
[JsonSerializable(typeof(CircleLine))]
[JsonSerializable(typeof(CircleLine[]))]
[JsonSerializable(typeof(CircleFactoryRecord))]
[JsonSerializable(typeof(CircleEntry))]
[JsonSerializable(typeof(CircleEntry[]))]
[JsonSerializable(typeof(CircleInvitesRecord))]
[JsonSerializable(typeof(CircleCodeInvite))]
[JsonSerializable(typeof(CircleMachine))]
[JsonSerializable(typeof(CircleMachine[]))]
[JsonSerializable(typeof(RedeemOutcome))]
[JsonSerializable(typeof(RedeemResult))]
[JsonSerializable(typeof(CircleChanged))]
[JsonSerializable(typeof(CircleSpoke))]
[JsonSerializable(typeof(CircleMembershipRevoked))]
[JsonSerializable(typeof(CircleRoomJoined))]
[JsonSerializable(typeof(CircleRoomLeft))]
internal sealed partial class CirclesJson : JsonSerializerContext;
