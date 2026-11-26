namespace IntoChat.OS.Host.Execution;

/// <summary>
/// Configuration for Azure Container Apps (ACA) Dynamic Sessions (Kata MicroVMs) headless cloud fallback
/// shared by Plane 1 (<c>AcaDynamicSessionsSandbox</c>) and Plane 2 (<c>AcaDynamicSessionsCandidateHost</c>).
/// Dormant unless <see cref="PoolManagementEndpoint"/> is configured.
/// </summary>
public sealed class AcaDynamicSessionsOptions
{
    public const string SectionName = "AcaDynamicSessions";
    public const string HttpClientName = "AcaDynamicSessions";
    public const string DefaultApiVersion = "2024-02-02-preview";
    public const string DefaultTokenScope = "https://dynamicsessions.io/.default";
    public const string DefaultTokenResource = "https://dynamicsessions.io";
    public const int DefaultMaxPayloadBytes = 4 * 1024 * 1024;
    public const int MinMaxPayloadBytes = 1024;

    public static readonly TimeSpan DefaultExecutionTimeout = TimeSpan.FromSeconds(60);
    public static readonly TimeSpan DefaultSessionHealthTimeout = TimeSpan.FromSeconds(120);
    public static readonly TimeSpan MaxAllowedTimeout = TimeSpan.FromMinutes(15);

    /// <summary>
    /// Azure Container Apps Dynamic Sessions pool management endpoint, e.g.
    /// <c>https://eastus.dynamicsessions.io/subscriptions/{sub}/resourceGroups/{rg}/sessionPools/{pool}</c>.
    /// When null or whitespace, ACA Dynamic Sessions fallback remains dormant.
    /// </summary>
    public string? PoolManagementEndpoint { get; set; }

    /// <summary>Azure Dynamic Sessions management plane API version query parameter.</summary>
    public string ApiVersion { get; set; } = DefaultApiVersion;

    /// <summary>OAuth2/Entra ID scope used to acquire bearer tokens for the pool management plane.</summary>
    public string TokenScope { get; set; } = DefaultTokenScope;

    /// <summary>Optional user-assigned Managed Identity client ID for token acquisition.</summary>
    public string? ManagedIdentityClientId { get; set; }

    /// <summary>Strict per-execution timeout enforced on <c>/executions</c> requests.</summary>
    public TimeSpan ExecutionTimeout { get; set; } = DefaultExecutionTimeout;

    /// <summary>Strict readiness timeout enforced when waiting for a Plane 2 candidate session to become healthy.</summary>
    public TimeSpan SessionHealthTimeout { get; set; } = DefaultSessionHealthTimeout;

    /// <summary>Maximum serialized UTF-8 byte size permitted for outbound execution payloads.</summary>
    public int MaxPayloadBytes { get; set; } = DefaultMaxPayloadBytes;

    /// <summary>Requires HTTPS on <see cref="PoolManagementEndpoint"/> so bearer tokens never traverse plaintext HTTP.</summary>
    public bool RequireHttps { get; set; } = true;

    /// <summary>Returns true when <see cref="PoolManagementEndpoint"/> has been explicitly configured.</summary>
    public bool IsConfigured => !string.IsNullOrWhiteSpace(PoolManagementEndpoint);

    public Uri GetValidatedEndpointUri()
    {
        if (!IsConfigured)
        {
            throw new AcaDynamicSessionsNotConfiguredException(
                "ACA Dynamic Sessions PoolManagementEndpoint is not configured.");
        }

        if (!Uri.TryCreate(PoolManagementEndpoint!.Trim(), UriKind.Absolute, out var uri))
        {
            throw new ArgumentException(
                "PoolManagementEndpoint must be a valid absolute URI.",
                nameof(PoolManagementEndpoint));
        }

        if (RequireHttps && !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException(
                "PoolManagementEndpoint must use the https scheme so bearer tokens are never sent over plaintext HTTP.",
                nameof(PoolManagementEndpoint));
        }

        if (!string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException(
                "PoolManagementEndpoint must use an HTTP(S) URI scheme.",
                nameof(PoolManagementEndpoint));
        }

        if (!string.IsNullOrEmpty(uri.UserInfo))
        {
            throw new ArgumentException(
                "PoolManagementEndpoint must not embed credentials in the URI.",
                nameof(PoolManagementEndpoint));
        }

        if (!string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment))
        {
            throw new ArgumentException(
                "PoolManagementEndpoint must be a base pool URI without query string or fragment.",
                nameof(PoolManagementEndpoint));
        }

        return uri;
    }

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(ApiVersion))
        {
            throw new ArgumentException("ApiVersion must be non-empty.", nameof(ApiVersion));
        }

        if (string.IsNullOrWhiteSpace(TokenScope))
        {
            throw new ArgumentException("TokenScope must be non-empty.", nameof(TokenScope));
        }

        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(ExecutionTimeout, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(ExecutionTimeout, MaxAllowedTimeout);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(SessionHealthTimeout, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(SessionHealthTimeout, MaxAllowedTimeout);
        ArgumentOutOfRangeException.ThrowIfLessThan(MaxPayloadBytes, MinMaxPayloadBytes);

        if (IsConfigured)
        {
            _ = GetValidatedEndpointUri();
        }
    }
}

/// <summary>Thrown fail-closed when ACA Dynamic Sessions is invoked without a configured pool management endpoint.</summary>
public sealed class AcaDynamicSessionsNotConfiguredException(string message) : InvalidOperationException(message);

/// <summary>Thrown when the ACA Dynamic Sessions pool management plane returns an error response.</summary>
public sealed class AcaDynamicSessionsException : InvalidOperationException
{
    public AcaDynamicSessionsException(string message, System.Net.HttpStatusCode? statusCode = null, Exception? innerException = null)
        : base(message, innerException)
    {
        StatusCode = statusCode;
    }

    public System.Net.HttpStatusCode? StatusCode { get; }
}
