using System.Text.Json;
using System.Text.Json.Serialization;

namespace IntoChat.OS.Modules.Identity.Contracts;

[GenerateSerializer, Alias("identity.account-id"), JsonConverter(typeof(AccountIdJsonConverter))]
public readonly record struct AccountId([property: Id(0)] string Value)
{
    public override string ToString() => Value ?? string.Empty;
}

public sealed class AccountIdJsonConverter : JsonConverter<AccountId>
{
    public override AccountId Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        => new(reader.GetString() ?? string.Empty);

    public override void Write(Utf8JsonWriter writer, AccountId value, JsonSerializerOptions options)
        => writer.WriteStringValue(value.Value ?? string.Empty);

    public override AccountId ReadAsPropertyName(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        => new(reader.GetString() ?? string.Empty);

    public override void WriteAsPropertyName(Utf8JsonWriter writer, AccountId value, JsonSerializerOptions options)
        => writer.WritePropertyName(value.Value ?? string.Empty);
}
