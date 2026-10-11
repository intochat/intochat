using System.Security.Cryptography;
using System.Text;

namespace DigitalBrain.Twilio;

[GenerateSerializer, Alias("twilio.form-field")]
internal sealed record TwilioFormField([property: Id(0)] string Name, [property: Id(1)] string Value);

// The raw delivery: the neuron owns the AuthToken, so it verifies the signature itself.
[GenerateSerializer, Alias("twilio.status-callback")]
internal sealed record TwilioStatusCallback(
    [property: Id(0)] string Url,
    [property: Id(1)] TwilioFormField[] Fields,
    [property: Id(2)] string Signature)
{
    public string? Field(string name) => Fields.FirstOrDefault(f => f.Name == name)?.Value;
}

[Alias("twilio.inbound"), Orleans.Metadata.DefaultGrainType("twilio")]
internal interface ITwilioInbound : IGrainWithStringKey
{
    // True when the delivery was signed by this account's AuthToken (a repeat is still true).
    Task<bool> AcceptStatus(TwilioStatusCallback callback);
}

// Twilio's documented webhook signature: the full URL Twilio called, then every POST field sorted by
// name with its value appended (no separators), HMAC-SHA1 keyed by the AuthToken, base64.
internal static class TwilioSignature
{
    public const string Header = "X-Twilio-Signature";

    public static string Compute(string authToken, string url, IEnumerable<TwilioFormField> fields)
    {
        var data = new StringBuilder(url);
        foreach (var field in fields.OrderBy(f => f.Name, StringComparer.Ordinal).ThenBy(f => f.Value, StringComparer.Ordinal))
        { data.Append(field.Name).Append(field.Value); }
        return Convert.ToBase64String(HMACSHA1.HashData(Encoding.UTF8.GetBytes(authToken), Encoding.UTF8.GetBytes(data.ToString())));
    }

    public static bool Matches(string authToken, string url, IEnumerable<TwilioFormField> fields, string presented)
        => CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(Compute(authToken, url, fields)), Encoding.ASCII.GetBytes(presented));
}

// Where Twilio reaches this deployment (the Platform's connections public origin, when set), one URL per brain.
internal sealed record TwilioCallbackOrigin(Uri? Origin)
{
    public Uri? StatusCallback(string brainId)
        => Origin is { } origin ? new Uri(origin, $"{TwilioModule.StatusPath.TrimStart('/')}/{Uri.EscapeDataString(brainId)}") : null;
}
