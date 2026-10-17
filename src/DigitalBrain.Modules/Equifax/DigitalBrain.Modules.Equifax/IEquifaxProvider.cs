namespace DigitalBrain.Equifax;

internal sealed record EquifaxCredential(string ClientId, string ClientSecret, Uri BaseUrl)
{
    private bool PrintMembers(System.Text.StringBuilder builder) => builder.Append("<redacted>") is not null;
}

// The message is a status ("unauthorized (401)", "503", "timeout"), never the provider's body.
internal sealed class EquifaxProviderException(string detail) : Exception(detail);

internal interface IEquifaxProvider
{
    Task AcquireTokenAsync(EquifaxCredential credential, CancellationToken ct);

    Task<string> ReadReportAsync(EquifaxCredential credential, string documentNumber, CancellationToken ct);
}
