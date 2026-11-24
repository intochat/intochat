using System.Text;
using System.Text.Json;

namespace IntoChat.OS.Modules.Apps;

// A model's reply read as JSON, for the catalog's writers that ask a model for one object (the Creator's agents, the policy writer).
public static class ModelReply
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    // Models sometimes wrap the JSON in a code fence or add prose around it; read the first complete object.
    public static T Parse<T>(string reply)
    {
        ArgumentNullException.ThrowIfNull(reply);
        var start = reply.IndexOf('{', StringComparison.Ordinal);
        if (start < 0) { throw new InvalidDataException("The reply contains no JSON object."); }
        var reader = new Utf8JsonReader(Encoding.UTF8.GetBytes(reply[start..]));
        using var document = JsonDocument.ParseValue(ref reader);
        return document.Deserialize<T>(Json) ?? throw new InvalidDataException("The reply's JSON object is empty.");
    }
}
