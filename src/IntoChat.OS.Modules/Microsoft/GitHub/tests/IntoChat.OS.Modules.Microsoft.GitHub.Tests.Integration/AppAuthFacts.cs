using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using IntoChat.OS.Framework.Platform.Integrations;
using IntoChat.OS.Framework.Platform.Secrets;
using IntoChat.OS.Microsoft.GitHub;
using IntoChat.OS.Authority.Secrets;
using IntoChat.OS.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace IntoChat.OS.Modules.Microsoft.GitHub.Tests.Integration;

public sealed class AppAuthFacts(GitHubHost host)
{
    [Fact]
    public async Task AppAuthSignsWithTheKeyReleasedFromTheRegistration()
    {
        var ct = TestContext.Current.CancellationToken;
        var appKey = host.AppKey;
        var brain = host.Brain;
        var handler = new CapturingHandler();
        using var tokens = new GitHubInstallationTokens(brain.Services.GetRequiredService<GitHubAppRegistration>(), handler, null);

        await Assert.ThrowsAsync<GitHubAccessDeniedException>(() => tokens.GetTokenAsync(Repository(appId: 7), refresh: true, ct));

        var jwt = Assert.IsType<string>(handler.Bearer).Split('.');
        Assert.True(appKey.VerifyData(Encoding.ASCII.GetBytes($"{jwt[0]}.{jwt[1]}"), Base64UrlDecode(jwt[2]), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1));
        Assert.Equal("7", JsonDocument.Parse(Base64UrlDecode(jwt[1])).RootElement.GetProperty("iss").GetString());
        Assert.Equal("/app/installations/9/access_tokens", handler.Path);
    }

    [Trait("host", "own")]
    [Fact]
    public async Task AnUnregisteredAppReportsUnavailableAndSendsNothing()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await Start(null, null, ct);
        var handler = new CapturingHandler();
        using var tokens = new GitHubInstallationTokens(brain.Services.GetRequiredService<GitHubAppRegistration>(), handler, null);

        await Assert.ThrowsAsync<GitHubUnavailableException>(() => tokens.GetTokenAsync(Repository(appId: 7), refresh: true, ct));

        Assert.Null(handler.Path);
    }

    [Fact]
    public async Task ARepositoryOfAnotherAppIsNeverSignedForByTheRegisteredKey()
    {
        var ct = TestContext.Current.CancellationToken;
        var appKey = host.AppKey;
        var brain = host.Brain;
        var handler = new CapturingHandler();
        using var tokens = new GitHubInstallationTokens(brain.Services.GetRequiredService<GitHubAppRegistration>(), handler, null);

        await Assert.ThrowsAsync<GitHubUnavailableException>(() => tokens.GetTokenAsync(Repository(appId: 8), refresh: true, ct));

        Assert.Null(handler.Path);
    }

    [Fact]
    public async Task AFullTokenCacheEvictsTheLeastRecentlyUsedConnectionInsteadOfRefusing()
    {
        var ct = TestContext.Current.CancellationToken;
        var appKey = host.AppKey;
        var brain = host.Brain;
        using var tokens = new GitHubInstallationTokens(brain.Services.GetRequiredService<GitHubAppRegistration>(), new MintingHandler(), null, capacity: 2);

        for (var connection = 0; connection < 5; connection++)
        {
            Assert.Equal($"token-{connection + 1}", await tokens.GetTokenAsync(Repository(7) with { Key = $"connection-{connection}" }, refresh: false, ct));
            Assert.True(tokens.CachedConnections <= 2);
        }
        // The most recent connection is still cached; the first was evicted and mints again.
        Assert.Equal("token-5", await tokens.GetTokenAsync(Repository(7) with { Key = "connection-4" }, refresh: false, ct));
        Assert.Equal("token-6", await tokens.GetTokenAsync(Repository(7) with { Key = "connection-0" }, refresh: false, ct));
        tokens.Forget("connection-0");
        Assert.Equal("token-7", await tokens.GetTokenAsync(Repository(7) with { Key = "connection-0" }, refresh: false, ct));
    }

    [Fact]
    public async Task ARateLimitedTokenMintIsUnavailableNotDenied()
    {
        var ct = TestContext.Current.CancellationToken;
        var appKey = host.AppKey;
        var brain = host.Brain;
        using var tokens = new GitHubInstallationTokens(brain.Services.GetRequiredService<GitHubAppRegistration>(), new RateLimitedHandler(), null);

        await Assert.ThrowsAsync<GitHubUnavailableException>(() => tokens.GetTokenAsync(Repository(7), refresh: true, ct));
    }

    private static Task<ITestBrain> Start(string? privateKeyPem, string? appId, CancellationToken cancellationToken)
        => Compose(TestBrain.Create(), privateKeyPem, appId).StartAsync(cancellationToken);

    internal static TestBrain Compose(TestBrain brain, string? privateKeyPem, string? appId)
    {
        brain.WithModule<GitHubModule>();
        if (privateKeyPem is not null && appId is not null)
        {
            brain.WithIntegration(GitHubModule.Integration, "PrivateKeyPem", privateKeyPem)
                .WithIntegration(GitHubModule.Integration, "AppId", appId);
        }

        return brain;
    }

    private static GitHubRepository Repository(long appId)
        => new("repo", appId, 9, 11, "intochat", "intochat-os");

    private static byte[] Base64UrlDecode(string text)
        => Convert.FromBase64String(text.Replace('-', '+').Replace('_', '/').PadRight((text.Length + 3) / 4 * 4, '='));

    private sealed class MintingHandler : HttpMessageHandler
    {
        private int _minted;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = JsonSerializer.Serialize(new
            {
                token = $"token-{Interlocked.Increment(ref _minted)}",
                expires_at = DateTimeOffset.UtcNow.AddHours(1),
                repositories = new[] { new { id = 11 } },
                permissions = new { contents = "read", metadata = "read" },
            });
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Created) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
        }
    }

    private sealed class RateLimitedHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var response = new HttpResponseMessage(HttpStatusCode.Forbidden);
            response.Headers.Add("X-RateLimit-Remaining", "0");
            return Task.FromResult(response);
        }
    }

    private sealed class CapturingHandler : HttpMessageHandler
    {
        public string? Bearer { get; private set; }

        public string? Path { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Bearer = request.Headers.Authorization?.Parameter;
            Path = request.RequestUri?.AbsolutePath;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized));
        }
    }
}
