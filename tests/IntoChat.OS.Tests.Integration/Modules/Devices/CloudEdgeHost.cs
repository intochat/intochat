using System.Security.Cryptography;
using DigitalBrain;
using DigitalBrain.Access;
using DigitalBrain.Capabilities.Identity;
using DigitalBrain.Neurons;
using IntoChat.OS.Modules.Devices;
using IntoChat.OS.Modules.Identity.Accounts;
using IntoChat.OS.Modules.Identity.Contracts.Accounts;
using IntoChat.OS.Runtime;
using IntoChat.OS.Tests.Integration.Security;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace IntoChat.OS.Tests.Integration.Modules.Devices;

// A real OS host (the access-findings harness: full composition, in-process Kestrel and silo on ephemeral loopback ports) that
// trusts two issuers: "cloud", listed for service assertions and guest tickets, and "signin", listed for sign-in only.
public sealed class CloudEdgeHost : IAsyncLifetime
{
    public const string Audience = "https://os.test";
    public const string Cloud = "cloud";
    public const string SignInOnly = "signin";

    public ECDsa CloudKey { get; } = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    public ECDsa SignInKey { get; } = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    internal Gate20EdgeApprovalWallFacts.EdgeKernelHarness Host { get; private set; } = null!;

    public async ValueTask InitializeAsync()
    {
        Host = await Gate20EdgeApprovalWallFacts.EdgeKernelHarness.StartAsync(TestContext.Current.CancellationToken, builder =>
            builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                [AuthKeys.TrustedIssuers + ":0:Name"] = Cloud,
                [AuthKeys.TrustedIssuers + ":0:PublicKey"] = Convert.ToBase64String(CloudKey.ExportSubjectPublicKeyInfo()),
                [AuthKeys.TrustedIssuers + ":0:Audience"] = Audience,
                [AuthKeys.TrustedIssuers + ":0:Services"] = "true",
                [AuthKeys.TrustedIssuers + ":0:Guests"] = "true",
                [AuthKeys.TrustedIssuers + ":1:Name"] = SignInOnly,
                [AuthKeys.TrustedIssuers + ":1:PublicKey"] = Convert.ToBase64String(SignInKey.ExportSubjectPublicKeyInfo()),
                [AuthKeys.TrustedIssuers + ":1:Audience"] = Audience,
            }));
    }

    public async ValueTask DisposeAsync()
    {
        await Host.DisposeAsync();
        CloudKey.Dispose();
        SignInKey.Dispose();
    }

    public string Brain => Host.Scope;

    // The brain's owner mints a device code, as the shell does for a person.
    public async Task<Code> MintAsync()
    {
        var edge = Host.Services.GetRequiredService<EdgeAuthority>();
        using (edge.Enter(edge.Person(AccountSession.DefaultLogin, AccountSession.DefaultLogin, new BrainId(Brain))))
        {
            return await Host.Services.GetRequiredService<INeuronProxies>().Get<IDeviceEnrollment>(IDeviceEnrollment.Key(Brain)).Mint();
        }
    }

    public string Assert(string operation, HttpMethod method, string target, byte[] body, string issuer = Cloud, ECDsa? key = null,
        string audience = Audience, TimeProvider? clock = null)
    {
        clock ??= TimeProvider.System;
        var claims = new ServiceAssertionClaims(issuer, audience, operation, ServiceAssertion.Request(method.Method, target, body),
            Guid.NewGuid().ToString("N"), clock.GetUtcNow().AddSeconds(30));
        return ServiceAssertion.Scheme + " " + ServiceAssertion.Sign(claims, key ?? CloudKey, clock);
    }

    public async Task<HttpResponseMessage> SendAsync(HttpMethod method, string target, byte[]? body, string? authorization)
    {
        using var request = new HttpRequestMessage(method, target);
        if (body is not null)
        {
            request.Content = new ByteArrayContent(body);
            request.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
        }
        if (authorization is not null) { request.Headers.TryAddWithoutValidation("Authorization", authorization); }
        return await Host.Client.SendAsync(request, TestContext.Current.CancellationToken);
    }

    // Sends the request with a fresh assertion made for exactly it.
    public Task<HttpResponseMessage> SendAssertedAsync(string operation, HttpMethod method, string target, byte[]? body = null)
        => SendAsync(method, target, body, Assert(operation, method, target, body ?? []));
}
