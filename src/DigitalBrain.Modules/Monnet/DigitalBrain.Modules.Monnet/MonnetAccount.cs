namespace DigitalBrain.Monnet;

// One merchant's credentials for one provider call; never persisted, never printed.
internal sealed record MonnetAccount(Uri BaseUrl, string MerchantId, string ApiKey)
{
    public override string ToString() => $"MonnetAccount {{ BaseUrl = {BaseUrl}, MerchantId = {MerchantId} }}";
}

// The pasted `credentials` value: MerchantId=…;ApiKey=…[;BaseUrl=…], read like a connection string.
internal sealed record MonnetCredentials(string MerchantId, string ApiKey, Uri? BaseUrl)
{
    public const string Malformed = "The Monnet credentials must read MerchantId=…;ApiKey=… (optionally ;BaseUrl=…).";

    public static bool TryParse(string value, out MonnetCredentials credentials)
    {
        credentials = null!;
        string? merchant = null, key = null;
        Uri? baseUrl = null;
        foreach (var part in value.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var separator = part.IndexOf('=', StringComparison.Ordinal);
            if (separator <= 0) { return false; }
            var name = part[..separator].Trim();
            var field = part[(separator + 1)..].Trim();
            if (field.Length == 0) { return false; }
            switch (name.ToLowerInvariant())
            {
                case "merchantid" when merchant is null: merchant = field; break;
                case "apikey" when key is null: key = field; break;
                case "baseurl" when baseUrl is null:
                    if (!Uri.TryCreate(field, UriKind.Absolute, out baseUrl)) { return false; }
                    break;
                default: return false;
            }
        }
        if (merchant is null || key is null) { return false; }
        credentials = new(merchant, key, baseUrl);
        return true;
    }

    public override string ToString() => $"MonnetCredentials {{ MerchantId = {MerchantId} }}";
}
