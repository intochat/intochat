using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using DigitalBrain.Capabilities.Secrets;
using DigitalBrain.Identity.Local.Custody;
using IntoChat.OS.Modules.Infrastructure.Secrets.Contracts;

namespace IntoChat.OS.Modules.Infrastructure.Secrets;

/// <summary>Configuration options for <see cref="BitwardenSecretStoreBackend"/>.</summary>
public sealed class BitwardenSecretStoreOptions
{
    public const string SectionName = "IntoChatOS:Secrets:Bitwarden";

    public string OrganizationId { get; set; } = "";

    public string ProjectId { get; set; } = "";

    public string AccessToken { get; set; } = "";

    public string DeploymentId { get; set; } = "";

    public Uri ApiUrl { get; set; } = new("https://api.bitwarden.com");

    public Uri IdentityUrl { get; set; } = new("https://identity.bitwarden.com");

    public string OwnerKind { get; set; } = SecretCoordinate.DefaultOwnerKind;

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(OrganizationId))
        {
            throw new InvalidOperationException($"{SectionName}:OrganizationId must be configured.");
        }

        if (string.IsNullOrWhiteSpace(ProjectId))
        {
            throw new InvalidOperationException($"{SectionName}:ProjectId must be configured.");
        }

        if (string.IsNullOrWhiteSpace(AccessToken))
        {
            throw new InvalidOperationException($"{SectionName}:AccessToken must be configured.");
        }

        if (string.IsNullOrWhiteSpace(DeploymentId))
        {
            throw new InvalidOperationException($"{SectionName}:DeploymentId must be configured.");
        }

        if (ApiUrl is null || !ApiUrl.IsAbsoluteUri || !string.Equals(ApiUrl.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"{SectionName}:ApiUrl must be a valid HTTPS URI.");
        }

        if (IdentityUrl is null || !IdentityUrl.IsAbsoluteUri || !string.Equals(IdentityUrl.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"{SectionName}:IdentityUrl must be a valid HTTPS URI.");
        }
    }
}

/// <summary>
/// <see cref="ISecretStoreBackend"/> backed by Bitwarden Secrets Manager via <see cref="IBitwardenSecretsClient"/>.
/// Uses deterministic <see cref="SecretCoordinate"/> keys, serialized upsert locking, resilient note parsing,
/// and AEAD envelope sealing via <see cref="IMasterKeyCustody"/>.
/// </summary>
internal sealed class BitwardenSecretStoreBackend : ISecretStoreBackend, IDisposable
{
    private readonly IBitwardenSecretsClient _client;
    private readonly IMasterKeyCustody _custody;
    private readonly BitwardenSecretStoreOptions _options;
    private readonly SemaphoreSlim _writeGate = new(1, 1);

    public BitwardenSecretStoreBackend(
        IBitwardenSecretsClient client,
        IMasterKeyCustody custody,
        BitwardenSecretStoreOptions options)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _custody = custody ?? throw new ArgumentNullException(nameof(custody));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _options.Validate();
    }

    public void Dispose() => _writeGate.Dispose();

    public async ValueTask<SecretEntry?> GetAsync(string owner, string name, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(owner);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        var coordinate = SecretCoordinate.Format(_options.DeploymentId, owner, name, _options.OwnerKind);
        var record = await _client.GetByKeyAsync(coordinate, cancellationToken).ConfigureAwait(false);
        if (record is null)
        {
            return null;
        }

        var entry = Unseal(owner, name, record.Value);
        return string.Equals(entry.Name, name, StringComparison.Ordinal) ? entry : null;
    }

    public async ValueTask SetAsync(string owner, string name, string label, string value, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(owner);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        var coordinate = SecretCoordinate.Format(_options.DeploymentId, owner, name, _options.OwnerKind);
        var sealedValue = Seal(owner, name, label ?? "", value);
        var note = JsonSerializer.Serialize(new BitwardenSecretNote(_options.DeploymentId, _options.OwnerKind, name), BitwardenJsonContext.Default.BitwardenSecretNote);
        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await _client.UpsertAsync(coordinate, sealedValue, note, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    public async ValueTask<bool> RemoveAsync(string owner, string name, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(owner);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        var coordinate = SecretCoordinate.Format(_options.DeploymentId, owner, name, _options.OwnerKind);
        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await _client.DeleteByKeyAsync(coordinate, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    public async ValueTask<IReadOnlyList<string>> ListValuesAsync(string owner, CancellationToken cancellationToken = default)
    {
        var entries = await ListEntriesAsync(owner, cancellationToken).ConfigureAwait(false);
        return [.. entries.Select(e => e.Value)];
    }

    public async ValueTask<IReadOnlyList<SecretEntry>> ListEntriesAsync(string owner, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(owner);
        var prefix = SecretCoordinate.OwnerPrefix(_options.DeploymentId, owner, _options.OwnerKind);
        var records = await _client.ListByKeyPrefixAsync(prefix, cancellationToken).ConfigureAwait(false);
        var entries = new List<SecretEntry>(records.Count);
        foreach (var record in records)
        {
            if (string.IsNullOrWhiteSpace(record.Note))
            {
                continue;
            }

            BitwardenSecretNote? note;
            try
            {
                note = JsonSerializer.Deserialize(record.Note, BitwardenJsonContext.Default.BitwardenSecretNote);
            }
            catch (JsonException)
            {
                // Skip malformed or non-JSON notes rather than failing redaction for the entire owner.
                continue;
            }

            if (note is null || string.IsNullOrWhiteSpace(note.Name))
            {
                continue;
            }

            entries.Add(Unseal(owner, note.Name, record.Value));
        }

        return entries;
    }

    private string Seal(string owner, string name, string label, string value)
    {
        var aad = SecretCoordinate.FormatAad(_options.DeploymentId, owner, name);
        var payload = JsonSerializer.SerializeToUtf8Bytes(new SealedVaultEnvelope(name, label, value), SecretBackendJsonContext.Default.SealedVaultEnvelope);
        return Convert.ToBase64String(_custody.Wrap(owner, aad, payload));
    }

    private SecretEntry Unseal(string owner, string name, string sealedBase64)
    {
        var aad = SecretCoordinate.FormatAad(_options.DeploymentId, owner, name);
        var cipherBytes = Convert.FromBase64String(sealedBase64);
        var plainBytes = _custody.Unwrap(owner, aad, cipherBytes);
        var envelope = JsonSerializer.Deserialize(plainBytes, SecretBackendJsonContext.Default.SealedVaultEnvelope)
            ?? throw new CryptographicException("Invalid sealed vault envelope.");
        return new SecretEntry(envelope.Name, envelope.Label, envelope.Value);
    }
}

/// <summary>
/// AOT-safe HTTP client adapter for Bitwarden Secrets Manager REST API.
/// Exchanges Bitwarden machine access tokens (<c>0.&lt;client_id&gt;.&lt;client_secret&gt;[:...]</c>)
/// against <see cref="BitwardenSecretStoreOptions.IdentityUrl"/> (<c>/connect/token</c>) with token caching,
/// or uses a pre-exchanged bearer token directly.
/// </summary>
internal sealed class HttpBitwardenSecretsClient(
    HttpClient http,
    BitwardenSecretStoreOptions options,
    TimeProvider? clock = null) : IBitwardenSecretsClient, IDisposable
{
    public const string HttpClientName = "IntoChat.OS.BitwardenSecrets";

    private readonly HttpClient _http = http ?? throw new ArgumentNullException(nameof(http));
    private readonly BitwardenSecretStoreOptions _options = options ?? throw new ArgumentNullException(nameof(options));
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private readonly SemaphoreSlim _upsertGate = new(1, 1);
    private readonly SemaphoreSlim _tokenGate = new(1, 1);
    private string? _cachedBearerToken;
    private DateTimeOffset _tokenExpiresAt = DateTimeOffset.MinValue;

    public void Dispose()
    {
        _upsertGate.Dispose();
        _tokenGate.Dispose();
    }

    public async ValueTask<BitwardenSecretRecord?> GetByKeyAsync(string key, CancellationToken cancellationToken = default)
    {
        var all = await ListByKeyPrefixAsync(key, cancellationToken).ConfigureAwait(false);
        return all.FirstOrDefault(r => string.Equals(r.Key, key, StringComparison.Ordinal));
    }

    public async ValueTask<BitwardenSecretRecord> UpsertAsync(string key, string value, string note, CancellationToken cancellationToken = default)
    {
        await _upsertGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var existing = await GetByKeyAsync(key, cancellationToken).ConfigureAwait(false);
            if (existing is not null)
            {
                using var putRequest = await CreateRequestAsync(HttpMethod.Put, $"secrets/{existing.Id}", cancellationToken).ConfigureAwait(false);
                var updateBody = new BitwardenUpsertPayload(key, value, note, [_options.ProjectId]);
                putRequest.Content = new StringContent(
                    JsonSerializer.Serialize(updateBody, BitwardenJsonContext.Default.BitwardenUpsertPayload),
                    Encoding.UTF8,
                    "application/json");
                using var putResponse = await _http.SendAsync(putRequest, cancellationToken).ConfigureAwait(false);
                putResponse.EnsureSuccessStatusCode();
                var updatedJson = await putResponse.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                return ParseRecord(updatedJson) ?? new BitwardenSecretRecord(existing.Id, key, value, note);
            }

            using var postRequest = await CreateRequestAsync(HttpMethod.Post, $"organizations/{_options.OrganizationId}/secrets", cancellationToken).ConfigureAwait(false);
            var createBody = new BitwardenUpsertPayload(key, value, note, [_options.ProjectId]);
            postRequest.Content = new StringContent(
                JsonSerializer.Serialize(createBody, BitwardenJsonContext.Default.BitwardenUpsertPayload),
                Encoding.UTF8,
                "application/json");
            using var postResponse = await _http.SendAsync(postRequest, cancellationToken).ConfigureAwait(false);
            postResponse.EnsureSuccessStatusCode();
            var createdJson = await postResponse.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            return ParseRecord(createdJson) ?? new BitwardenSecretRecord(Guid.NewGuid().ToString("D"), key, value, note);
        }
        finally
        {
            _upsertGate.Release();
        }
    }

    public async ValueTask<bool> DeleteByKeyAsync(string key, CancellationToken cancellationToken = default)
    {
        var existing = await GetByKeyAsync(key, cancellationToken).ConfigureAwait(false);
        if (existing is null)
        {
            return false;
        }

        using var deleteRequest = await CreateRequestAsync(HttpMethod.Post, "secrets/delete", cancellationToken).ConfigureAwait(false);
        deleteRequest.Content = new StringContent(
            JsonSerializer.Serialize(new BitwardenDeletePayload([existing.Id]), BitwardenJsonContext.Default.BitwardenDeletePayload),
            Encoding.UTF8,
            "application/json");
        using var response = await _http.SendAsync(deleteRequest, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        return true;
    }

    public async ValueTask<IReadOnlyList<BitwardenSecretRecord>> ListByKeyPrefixAsync(string keyPrefix, CancellationToken cancellationToken = default)
    {
        using var request = await CreateRequestAsync(HttpMethod.Get, $"organizations/{_options.OrganizationId}/secrets", cancellationToken).ConfigureAwait(false);
        using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        var list = JsonSerializer.Deserialize(json, BitwardenJsonContext.Default.BitwardenSecretListResponse);
        if (list?.Data is null)
        {
            return [];
        }

        return
        [
            .. list.Data
                .Where(item => item.Key.StartsWith(keyPrefix, StringComparison.Ordinal))
                .Select(item => new BitwardenSecretRecord(item.Id, item.Key, item.Value, item.Note ?? "")),
        ];
    }

    private async Task<HttpRequestMessage> CreateRequestAsync(HttpMethod method, string relativePath, CancellationToken cancellationToken)
    {
        var token = await ResolveBearerTokenAsync(cancellationToken).ConfigureAwait(false);
        var uri = new Uri(_options.ApiUrl, relativePath);
        var request = new HttpRequestMessage(method, uri);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return request;
    }

    private async Task<string> ResolveBearerTokenAsync(CancellationToken cancellationToken)
    {
        if (!TryParseMachineCredentials(_options.AccessToken, out var clientId, out var clientSecret))
        {
            return _options.AccessToken;
        }

        var now = _clock.GetUtcNow();
        if (_cachedBearerToken is not null && now < _tokenExpiresAt)
        {
            return _cachedBearerToken;
        }

        await _tokenGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            now = _clock.GetUtcNow();
            if (_cachedBearerToken is not null && now < _tokenExpiresAt)
            {
                return _cachedBearerToken;
            }

            var tokenEndpoint = new Uri(_options.IdentityUrl, "connect/token");
            using var request = new HttpRequestMessage(HttpMethod.Post, tokenEndpoint)
            {
                Content = new FormUrlEncodedContent(new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["grant_type"] = "client_credentials",
                    ["scope"] = "api.secrets",
                    ["client_id"] = clientId,
                    ["client_secret"] = clientSecret,
                }),
            };

            using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            var parsed = JsonSerializer.Deserialize(json, BitwardenJsonContext.Default.BitwardenTokenResponse);
            if (parsed is null || string.IsNullOrWhiteSpace(parsed.AccessToken))
            {
                throw new InvalidOperationException("Bitwarden identity endpoint returned an empty access_token.");
            }

            var lifetime = parsed.ExpiresIn > 120 ? TimeSpan.FromSeconds(parsed.ExpiresIn - 60) : TimeSpan.FromSeconds(Math.Max(10, parsed.ExpiresIn));
            _cachedBearerToken = parsed.AccessToken;
            _tokenExpiresAt = now + lifetime;
            return _cachedBearerToken;
        }
        finally
        {
            _tokenGate.Release();
        }
    }

    internal static bool TryParseMachineCredentials(string token, out string clientId, out string clientSecret)
    {
        clientId = "";
        clientSecret = "";
        if (string.IsNullOrWhiteSpace(token) || !token.StartsWith("0.", StringComparison.Ordinal))
        {
            return false;
        }

        var colon = token.IndexOf(':', StringComparison.Ordinal);
        var credentialPart = colon > 0 ? token[2..colon] : token[2..];
        var dot = credentialPart.IndexOf('.', StringComparison.Ordinal);
        if (dot <= 0 || dot == credentialPart.Length - 1)
        {
            return false;
        }

        clientId = credentialPart[..dot];
        clientSecret = credentialPart[(dot + 1)..];
        return !string.IsNullOrWhiteSpace(clientId) && !string.IsNullOrWhiteSpace(clientSecret);
    }

    private static BitwardenSecretRecord? ParseRecord(string json)
    {
        var dto = JsonSerializer.Deserialize(json, BitwardenJsonContext.Default.BitwardenSecretDto);
        return dto is null ? null : new BitwardenSecretRecord(dto.Id, dto.Key, dto.Value, dto.Note ?? "");
    }
}

internal sealed record BitwardenSecretNote(string DeploymentId, string OwnerKind, string Name);
internal sealed record BitwardenUpsertPayload(string Key, string Value, string Note, string[] ProjectIds);
internal sealed record BitwardenDeletePayload(string[] Ids);
internal sealed record BitwardenSecretDto(string Id, string Key, string Value, string? Note);
internal sealed record BitwardenSecretListResponse(BitwardenSecretDto[]? Data);
internal sealed record BitwardenTokenResponse(
    [property: JsonPropertyName("access_token")] string AccessToken,
    [property: JsonPropertyName("expires_in")] int ExpiresIn);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(BitwardenSecretNote))]
[JsonSerializable(typeof(BitwardenUpsertPayload))]
[JsonSerializable(typeof(BitwardenDeletePayload))]
[JsonSerializable(typeof(BitwardenSecretDto))]
[JsonSerializable(typeof(BitwardenSecretListResponse))]
[JsonSerializable(typeof(BitwardenTokenResponse))]
internal sealed partial class BitwardenJsonContext : JsonSerializerContext;
