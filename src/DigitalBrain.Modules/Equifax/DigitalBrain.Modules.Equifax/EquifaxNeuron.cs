using System.Data.Common;
using System.Text;
using DigitalBrain.Equifax.Signals;
using DigitalBrain.Framework;
using DigitalBrain.Framework.Platform.Connections;
using DigitalBrain.Framework.Platform.Integrations;

namespace DigitalBrain.Equifax;

// Serves only connection keys {brain}/connection/equifax/{id}. A "credentials" connection reads with the
// "ClientId=…;ClientSecret=…" value sealed in its own state; a "deployment" connection reads with the
// deployment's Equifax integration (its sealed value is a placeholder and is ignored).
[GrainType("equifax")]
internal sealed class EquifaxNeuron(
    IEquifaxProvider provider, TimeProvider time, EquifaxModuleOptions options, IModuleIntegrations<EquifaxModule> integrations,
    [PersistentState("equifax", DigitalBrainNames.ClusterGrainStorage)] IPersistentState<EquifaxState> store)
    : ConnectionNeuron<EquifaxState>(store), IEquifax
{
    internal const int MaxReports = 50;
    internal const int MaxRawBytes = 32 * 1024;
    internal const string DeploymentMethod = "deployment";
    internal const string NotConfigured = "Equifax is not configured";

    public async Task<BureauReport> ReadCreditReport(ReadCreditReport query)
    {
        ArgumentNullException.ThrowIfNull(query);
        if (!DigitalBrain.Equifax.ReadCreditReport.IsDni(query.DocumentNumber))
        { throw new ArgumentException("DocumentNumber must be a Peruvian DNI: exactly 8 digits.", nameof(query)); }

        string raw;
        try { raw = await WithCredential(credential => provider.ReadReportAsync(credential, query.DocumentNumber, CancellationToken.None)); }
        catch (EquifaxProviderException error)
        {
            await Save(Snapshot, new BureauReportFailed(query.DocumentNumber, error.Message));
            throw new EquifaxUnavailableException(error.Message);
        }

        var report = new BureauReport(query.DocumentNumber, time.GetUtcNow(), Truncate(raw), BureauFlagsReader.Read(raw));
        Snapshot.Reports = [report, .. Snapshot.Reports.Take(MaxReports - 1)];
        await Save(Snapshot, new BureauReportRead(report));
        return report;
    }

    public Task<BureauReport[]> ReadRecent(int limit)
        => Task.FromResult(Snapshot.Reports.Take(Math.Clamp(limit, 0, MaxReports)).ToArray());

    protected override async Task<ConnectionProbeResult> ProbeAsync(Credential credential, CancellationToken ct)
    {
        try
        {
            await provider.AcquireTokenAsync(await Resolve(credential), ct);
            return ConnectionProbeResult.Connected();
        }
        catch (EquifaxProviderException error) { return ConnectionProbeResult.Failing(error.Message); }
    }

    private async Task<T> WithCredential<T>(Func<EquifaxCredential, Task<T>> call)
    {
        RequireOwnBrain();
        return await UseCredentialAsync(async (credential, _) => await call(await Resolve(credential)), CancellationToken.None);
    }

    // Not Ready surfaces as a provider failure, so a read publishes BureauReportFailed and a probe reports Failing.
    private async Task<EquifaxCredential> Resolve(Credential credential)
    {
        if (Snapshot.AuthMethodId != DeploymentMethod) { return Parse(credential.Value); }
        var integration = integrations.Get("equifax");
        var snapshot = await integration.ReadAsync();
        if (snapshot.Status != IntegrationStatus.Ready) { throw new EquifaxProviderException(NotConfigured); }
        snapshot.Settings.TryGetValue("BaseUrl", out var baseUrl);
        return new EquifaxCredential(
            await integration.ReadSecretAsync("ClientId"), await integration.ReadSecretAsync("ClientSecret"), BaseUrl(baseUrl));
    }

    private EquifaxCredential Parse(string value)
    {
        const string Shape = "The Equifax credentials must read ClientId=…;ClientSecret=….";
        var builder = new DbConnectionStringBuilder();
        try { builder.ConnectionString = value; }
        catch (ArgumentException) { throw new EquifaxUnavailableException(Shape); }
        string? Field(string name) => builder.TryGetValue(name, out var field) && field is string { Length: > 0 } text ? text : null;
        var clientId = Field("ClientId");
        var secret = Field("ClientSecret");
        if (clientId is null || secret is null) { throw new EquifaxUnavailableException(Shape); }
        return new(clientId, secret, BaseUrl(Field("BaseUrl")));
    }

    private Uri BaseUrl(string? value)
    {
        try { return options.Resolve(string.IsNullOrWhiteSpace(value) ? null : new Uri(value, UriKind.RelativeOrAbsolute)); }
        catch (Exception error) when (error is ArgumentException or UriFormatException) { throw new EquifaxUnavailableException(error.Message); }
    }

    private void RequireOwnBrain()
    {
        (string Brain, string Connector, string Connection) key;
        try { key = Key; }
        catch (ArgumentException) { throw new UnauthorizedAccessException("A canonical connection key is required."); }
        if (key.Connector != "equifax" || key.Brain != BrainScope.CurrentId())
        { throw new UnauthorizedAccessException("The connection belongs to another brain."); }
    }

    private static string Truncate(string raw)
    {
        if (Encoding.UTF8.GetByteCount(raw) <= MaxRawBytes) { return raw; }
        var bytes = Encoding.UTF8.GetBytes(raw);
        var length = MaxRawBytes;
        while (length > 0 && (bytes[length] & 0xC0) == 0x80) { length--; }
        return Encoding.UTF8.GetString(bytes, 0, length);
    }
}
