using System.Data.Common;

namespace DigitalBrain.Twilio;

[GenerateSerializer, Alias("twilio.unavailable")]
public sealed class TwilioUnavailableException(string message) : InvalidOperationException(message);

// The account a send goes out on. Never persisted or logged: it carries the AuthToken.
internal sealed record TwilioAccount(string AccountSid, string AuthToken, string From)
{
    public const string Format = "AccountSid=...;AuthToken=...;From=...";

    // A pasted `credentials` value, read like a connection string.
    public static TwilioAccount? TryParse(string value)
    {
        var builder = new DbConnectionStringBuilder();
        try { builder.ConnectionString = value; }
        catch (ArgumentException) { return null; }
        string? Field(string name) => builder.TryGetValue(name, out var field) && field is string text && text.Trim().Length > 0 ? text.Trim() : null;
        var sid = Field("AccountSid");
        var token = Field("AuthToken");
        var from = Field("From") ?? Field("FromNumber");
        return sid is null || token is null || from is null ? null : new(sid, token, from);
    }

    public override string ToString() => $"TwilioAccount {{ AccountSid = {AccountSid}, From = {From} }}";
}
