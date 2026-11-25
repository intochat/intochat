using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using DigitalBrain;
using DigitalBrain.Access;
using DigitalBrain.Brains;
using DigitalBrain.Capabilities.Identity;
using DigitalBrain.Neurons;
using DigitalBrain.Runtime.Orleans;
using IntoChat.OS.Host.Callers;
using IntoChat.OS.Modules.Identity.Accounts;
using IntoChat.OS.Modules.Identity.Contracts.Accounts;
using IntoChat.OS.Modules.Time.Reminders;
using IntoChat.OS.Runtime;
using IntoChat.OS.Tests.Integration.Security;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace IntoChat.OS.Tests.Integration.Modules.Identity;

/// <summary>
/// End-to-end integration tests for Slice OSS-2:
/// - Day-0 instant boot (GET / auto-mints an HttpOnly, SameSite=Lax ephemeral session cookie in RAM)
/// - Residency metadata surface on /identity/session and /identity/residency
/// - Zero orphan try-* disk files or durable directory entries while ephemeral
/// - Structured HTTP 428 CLAIM_REQUIRED upgrade prompts on external OAuth connectors & durable secrets
/// - Background schedule/automation gating via IBrainCapabilityPolicy
/// - In-place promotion via POST /identity/claim preserving the exact BrainId and state into durable disk storage
/// </summary>
public sealed class BrainResidencyIntegrationFacts : IAsyncLifetime
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private Gate20EdgeApprovalWallFacts.EdgeKernelHarness host = null!;

    public async ValueTask InitializeAsync()
    {
        host = await Gate20EdgeApprovalWallFacts.EdgeKernelHarness.StartAsync(Ct, builder =>
            builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                [AuthKeys.Day0EphemeralTry] = "true",
            }));
    }

    public async ValueTask DisposeAsync()
    {
        if (host is not null)
        {
            await host.DisposeAsync();
        }
    }

    private HttpClient NewBrowser() => new(new HttpClientHandler
    {
        AllowAutoRedirect = false,
        UseCookies = true,
        CookieContainer = new CookieContainer(),
    })
    {
        BaseAddress = host.BaseAddress,
    };

    [Fact]
    public async Task Day0Boot_AutoMintsEphemeralBrainInRam_GatesExternalCapabilitiesWith428_AndPromotesInPlaceOnClaim()
    {
        using var browser = NewBrowser();
        var storeDir = host.Services.GetRequiredService<IOptions<BrainStoreOptions>>().Value.Directory;

        // 1. Unauthenticated visitor hits GET /identity/session -> Day-0 bootstrap auto-mints an HttpOnly, SameSite=Lax ephemeral session.
        var cookieName = host.Services.GetRequiredService<IOptions<IdentityHostOptions>>().Value.CookieName;
        using var sessionResp = await browser.GetAsync("/identity/session", Ct);
        Assert.Equal(HttpStatusCode.OK, sessionResp.StatusCode);
        Assert.True(sessionResp.Headers.TryGetValues("Set-Cookie", out var setCookies));
        var setCookieHeader = string.Join("; ", setCookies);
        Assert.Contains(cookieName + "=", setCookieHeader, StringComparison.Ordinal);
        Assert.Contains("httponly", setCookieHeader, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("samesite=lax", setCookieHeader, StringComparison.OrdinalIgnoreCase);
        Assert.True(sessionResp.Headers.CacheControl?.NoStore == true);

        using var sessionDoc = JsonDocument.Parse(await sessionResp.Content.ReadAsStringAsync(Ct));
        var root = sessionDoc.RootElement;
        var ephemeralBrainId = root.GetProperty("brainId").GetString();
        Assert.False(string.IsNullOrWhiteSpace(ephemeralBrainId));
        Assert.True(GuestSurface.IsGuestPrincipal(ephemeralBrainId));

        var residency = root.GetProperty("residency");
        Assert.True(residency.GetProperty("isEphemeral").GetBoolean());
        Assert.True(residency.GetProperty("ttlSeconds").GetInt64() > 0);
        Assert.Equal(15, residency.GetProperty("remainingTurns").GetInt32());
        Assert.Equal(16 * 1024 * 1024, residency.GetProperty("memoryLimitBytes").GetInt64());

        // 2. Dedicated GET /identity/residency surface matches for Slice OSS-3 Top Guest Pill binding.
        var residencySnap = await browser.GetFromJsonAsync<BrainResidencySnapshot>("/identity/residency", Ct);
        Assert.NotNull(residencySnap);
        Assert.True(residencySnap.IsEphemeral);
        Assert.Equal(15, residencySnap.RemainingTurns);
        Assert.Equal(16 * 1024 * 1024, residencySnap.MemoryLimitBytes);

        // 3. Zero files exist on disk for the ephemeral brain — pure RAM residency (matches BrainStorageKeys.DirectoryOf).
        var brainHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(ephemeralBrainId!))).ToLowerInvariant();
        var brainDiskDir = Path.Combine(storeDir, brainHash);
        Assert.False(Directory.Exists(brainDiskDir));

        // 4. External OAuth connectors (login, probe, disconnect) cleanly return HTTP 428 Precondition Required + CLAIM_REQUIRED (not opaque 403).
        using var connectorResp = await browser.PostAsJsonAsync(
            $"/brains/{ephemeralBrainId}/connections/gh-main/login",
            new { connectorId = "github", authMethodId = "oauth" },
            Ct);
        Assert.Equal(HttpStatusCode.PreconditionRequired, connectorResp.StatusCode);
        using (var claimDoc = JsonDocument.Parse(await connectorResp.Content.ReadAsStringAsync(Ct)))
        {
            Assert.Equal(CapabilityDecision.ClaimRequiredCode, claimDoc.RootElement.GetProperty("code").GetString());
            Assert.True(claimDoc.RootElement.GetProperty("requiresClaim").GetBoolean());
            Assert.Equal(nameof(BrainCapability.ExternalConnector), claimDoc.RootElement.GetProperty("capability").GetString());
            Assert.Equal("github", claimDoc.RootElement.GetProperty("resource").GetString());
            Assert.Equal("/identity/claim", claimDoc.RootElement.GetProperty("claimEndpoint").GetString());
        }

        using var probeResp = await browser.PostAsync($"/brains/{ephemeralBrainId}/connections/github/gh-main/probe", content: null, Ct);
        Assert.Equal(HttpStatusCode.PreconditionRequired, probeResp.StatusCode);
        using var disconnectResp = await browser.PostAsync($"/brains/{ephemeralBrainId}/connections/github/gh-main/disconnect", content: null, Ct);
        Assert.Equal(HttpStatusCode.PreconditionRequired, disconnectResp.StatusCode);

        // 5. Durable vault secrets also return HTTP 428 CLAIM_REQUIRED.
        using var secretResp = await browser.PostAsJsonAsync(
            $"/secrets/{ephemeralBrainId}",
            new { name = "API_KEY", label = "API Key", value = "sk-test-123" },
            Ct);
        Assert.Equal(HttpStatusCode.PreconditionRequired, secretResp.StatusCode);
        using (var secretClaimDoc = JsonDocument.Parse(await secretResp.Content.ReadAsStringAsync(Ct)))
        {
            Assert.Equal(CapabilityDecision.ClaimRequiredCode, secretClaimDoc.RootElement.GetProperty("code").GetString());
            Assert.Equal(nameof(BrainCapability.DurableSecrets), secretClaimDoc.RootElement.GetProperty("capability").GetString());
        }

        // 6. Background automations / schedules throw ClaimRequiredException for ephemeral brains.
        var edge = host.Services.GetRequiredService<EdgeAuthority>();
        var proxies = host.Services.GetRequiredService<INeuronProxies>();
        using (edge.Enter(edge.Guest(ephemeralBrainId, ephemeralBrainId, new BrainId(ephemeralBrainId))))
        {
            var reminder = proxies.Get<IReminder>($"{ephemeralBrainId}/daily-brief");
            var claimError = await Assert.ThrowsAsync<ClaimRequiredException>(() =>
                reminder.Start(TimeSpan.FromHours(1), TimeSpan.FromHours(24)));
            Assert.Equal(BrainCapability.BackgroundAutomation, claimError.Decision.Capability);
            Assert.True(claimError.Decision.RequiresClaim);
        }

        // Still zero files on disk before claim!
        Assert.False(Directory.Exists(brainDiskDir));

        // 7. In-place promotion via POST /identity/claim (password registration): exact BrainId preserved!
        var username = "claimer-" + Guid.NewGuid().ToString("N")[..8];
        using var claimResp = await browser.PostAsJsonAsync(
            "/identity/claim",
            new { principalId = username, password = "correct-horse-battery-staple", displayName = "Claimer User" },
            Ct);
        Assert.Equal(HttpStatusCode.OK, claimResp.StatusCode);
        using (var claimedDoc = JsonDocument.Parse(await claimResp.Content.ReadAsStringAsync(Ct)))
        {
            Assert.Equal(ephemeralBrainId, claimedDoc.RootElement.GetProperty("brainId").GetString());
            Assert.Equal(username, claimedDoc.RootElement.GetProperty("principalId").GetString());
            Assert.True(claimedDoc.RootElement.GetProperty("claimed").GetBoolean());
            Assert.False(claimedDoc.RootElement.GetProperty("residency").GetProperty("isEphemeral").GetBoolean());
        }

        // 7b. A second ephemeral visitor attempting to register-claim with the already-registered username is rejected (409), even with the same password.
        using var browser2 = NewBrowser();
        using var sessionResp2 = await browser2.GetAsync("/identity/session", Ct);
        Assert.Equal(HttpStatusCode.OK, sessionResp2.StatusCode);
        using var duplicateClaimResp = await browser2.PostAsJsonAsync(
            "/identity/claim",
            new { principalId = username, password = "correct-horse-battery-staple", displayName = "Duplicate Claimer" },
            Ct);
        Assert.Equal(HttpStatusCode.Conflict, duplicateClaimResp.StatusCode);

        // 8. Exact BrainId was promoted in-place from RAM to encrypted durable disk storage!
        Assert.True(Directory.Exists(brainDiskDir));
        Assert.True(File.Exists(Path.Combine(brainDiskDir, "brain.key")));
        Assert.True(File.Exists(Path.Combine(brainDiskDir, "current")));

        // 9. Brain neuron state now reports Persistent(username) and session reports isEphemeral = false.
        var brainState = await edge.AsKernel(
            new BrainId(ephemeralBrainId),
            () => proxies.Get<IBrain>(IBrain.Key(ephemeralBrainId)).Read());
        var persistent = Assert.IsType<BrainResidency.Persistent>(brainState.Residency);
        Assert.Equal(username, persistent.OwnerPrincipalId);

        var afterResidency = await browser.GetFromJsonAsync<BrainResidencySnapshot>("/identity/residency", Ct);
        Assert.NotNull(afterResidency);
        Assert.False(afterResidency.IsEphemeral);

        // 10. Durable secrets are now unlocked for the claimed owner!
        using var secretAfterResp = await browser.PostAsJsonAsync(
            $"/secrets/{username}",
            new { name = "API_KEY", label = "API Key", value = "sk-live-456" },
            Ct);
        Assert.Equal(HttpStatusCode.OK, secretAfterResp.StatusCode);
    }
}
