using System.Text.Json;
using System.Text.Json.Serialization;
using IntoChat.OS.Modules.AI.Models;
using IntoChat.OS.Modules.Identity.Contracts;

namespace IntoChat.OS.Modules.Flutter.Conversations;

[GenerateSerializer, Alias("conversation.entry-id"), JsonConverter(typeof(EntryIdJsonConverter))]
public readonly record struct EntryId([property: Id(0)] string Value)
{
    public override string ToString() => Value ?? string.Empty;
}

public sealed class EntryIdJsonConverter : JsonConverter<EntryId>
{
    public override EntryId Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        => new(reader.GetString() ?? string.Empty);

    public override void Write(Utf8JsonWriter writer, EntryId value, JsonSerializerOptions options)
        => writer.WriteStringValue(value.Value ?? string.Empty);

    public override EntryId ReadAsPropertyName(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        => new(reader.GetString() ?? string.Empty);

    public override void WriteAsPropertyName(Utf8JsonWriter writer, EntryId value, JsonSerializerOptions options)
        => writer.WritePropertyName(value.Value ?? string.Empty);
}

[JsonPolymorphic(TypeDiscriminatorPropertyName = "$by")]
[JsonDerivedType(typeof(Participant.Human), "person")]
[JsonDerivedType(typeof(Participant.Assistant), "agent")]
public abstract record Participant
{
    public sealed record Human(AccountId Account) : Participant;
    public sealed record Assistant(string AgentId) : Participant;
}

public enum Visibility { Everyone, Agents, Owner }

[JsonPolymorphic(TypeDiscriminatorPropertyName = "$entry")]
[JsonDerivedType(typeof(Said), "said")]
[JsonDerivedType(typeof(Called), "called")]
[JsonDerivedType(typeof(Returned), "returned")]
[JsonDerivedType(typeof(Summarized), "summarized")]
public abstract record ConversationEntry(EntryId Id, Participant By, DateTimeOffset At, Visibility Visible);

public sealed record AttachmentRef(
    string Id,
    string Kind,
    string FileName,
    string MediaType,
    long SizeBytes,
    string Url,
    int? DurationMs = null,
    string? Transcript = null,
    string? Sha256 = null);

public sealed record Said(
    EntryId Id,
    Participant By,
    DateTimeOffset At,
    string Text,
    ModelId? Model = null,
    EntryId? Replaces = null,
    AttachmentRef[]? Attachments = null,
    Visibility Visible = Visibility.Everyone) : ConversationEntry(Id, By, At, Visible);

public sealed record Called(
    EntryId Id,
    Participant By,
    DateTimeOffset At,
    string Tool,
    string ArgsJson) : ConversationEntry(Id, By, At, Visibility.Agents);

public sealed record Returned(
    EntryId Id,
    Participant By,
    DateTimeOffset At,
    string Tool,
    string ResultJson,
    bool Ok,
    CardEntry? Card) : ConversationEntry(Id, By, At, Visibility.Everyone);

public sealed record Summarized(
    EntryId Id,
    Participant By,
    DateTimeOffset At,
    string Summary,
    EntryId UpTo) : ConversationEntry(Id, By, At, Visibility.Agents);

public sealed record CardEntry(string Kind, string Title, string? NodeJson);

// Entry ids are derived from the run, so a call pairs with its result and a run finds its entries without extra members.
public static class EntryIds
{
    private const string PostPrefix = "post/";
    private const string VoicePrefix = "voice/";

    public static EntryId Said(string runId) => new(runId + "/said");
    public static EntryId Reply(string runId) => new(runId + "/reply");
    public static EntryId Call(string runId, string callId) => new($"{runId}/call/{callId}");
    public static EntryId Result(string runId, string callId) => new($"{runId}/result/{callId}");
    public static EntryId Summary(string runId) => new(runId + "/summary");
    public static EntryId Post(string id) => new(PostPrefix + id);
    public static EntryId Heard(string id) => new($"{VoicePrefix}{id}/heard");
    public static EntryId Spoken(string id) => new($"{VoicePrefix}{id}/spoken");

    public static string? RunOf(EntryId id)
    {
        var value = id.Value;
        if (string.IsNullOrEmpty(value)
            || value.StartsWith(PostPrefix, StringComparison.Ordinal)
            || value.StartsWith(VoicePrefix, StringComparison.Ordinal))
        {
            return null;
        }
        var slash = value.IndexOf('/', StringComparison.Ordinal);
        return slash > 0 ? value[..slash] : null;
    }

    public static string? CallOf(EntryId id)
    {
        var value = id.Value;
        if (string.IsNullOrEmpty(value)
            || value.StartsWith(PostPrefix, StringComparison.Ordinal)
            || value.StartsWith(VoicePrefix, StringComparison.Ordinal))
        {
            return null;
        }
        var first = value.IndexOf('/', StringComparison.Ordinal);
        if (first <= 0 || first + 1 >= value.Length)
        {
            return null;
        }
        var rest = value.AsSpan(first + 1);
        if (rest.StartsWith("call/", StringComparison.Ordinal) && rest.Length > 5)
        {
            return rest[5..].ToString();
        }
        if (rest.StartsWith("result/", StringComparison.Ordinal) && rest.Length > 7)
        {
            return rest[7..].ToString();
        }
        return null;
    }
}
