using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using DigitalBrain.Authority.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Server.Kestrel.Https;
using Microsoft.Extensions.DependencyInjection;
using DigitalBrain.Framework.Platform.Identity;
using DigitalBrain.Authority.Identity.Configuration;
using System.Net;

namespace DigitalBrain.Authority.Tests.Integration.Identity;

[Trait("host", "own")]
public sealed class RuntimeMachineAuthenticationFacts
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MachineCertificateCanEnsureOnlyConfiguredScopeAndCannotEnterWorkspace(bool attachStorage)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await TestBrain.Create().StartAsync(ct);
        using var key = RSA.Create(2048);
        var certificateRequest = new CertificateRequest("CN=localhost", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        certificateRequest.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));
        certificateRequest.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new System.Security.Cryptography.OidCollection
        {
            new("1.3.6.1.5.5.7.3.1"), new("1.3.6.1.5.5.7.3.2")
        }, false));
        using var generated = certificateRequest.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddHours(1));
        using var certificate = X509CertificateLoader.LoadPkcs12(generated.Export(X509ContentType.Pkcs12), null);
        var builder = WebApplication.CreateBuilder();
        builder.Configuration[AuthOptions.PostureKey] = "Secured";
        builder.Configuration[HostedRuntimeOptions.SectionName + ":AccountId"] = "hosted-account";
        builder.Configuration[HostedRuntimeOptions.SectionName + ":BrainId"] = "hosted-brain";
        builder.Configuration[HostedRuntimeOptions.SectionName + ":RuntimeId"] = "allocation-one";
        var stateDirectory = Path.Combine(Path.GetTempPath(), "intochat-hosted-test-" + Guid.NewGuid().ToString("N"));
        builder.Configuration[HostedRuntimeOptions.SectionName + ":StateDirectory"] = stateDirectory;
        builder.Configuration[HostedRuntimeOptions.SectionName + ":ControllerCertificateSha256"] = certificate.GetCertHashString(HashAlgorithmName.SHA256);
        builder.Services.AddSingleton(brain.Services.GetRequiredService<IIdentityClient>());
        builder.Services.AddSingleton(brain.Services.GetRequiredService<BrainBirths>());
        builder.Services.AddSingleton(brain.Services.GetRequiredService<PrincipalPasswords>());
        if (attachStorage)
        {
            builder.Services.AddSingleton(brain.Services.GetRequiredService<DigitalBrain.Authority.Storage.BrainLogs>());
            builder.Services.AddSingleton(brain.Services.GetRequiredService<Orleans.Serialization.Serializer>());
            builder.Services.AddKeyedSingleton<Orleans.Storage.IGrainStorage>(DigitalBrain.Framework.DigitalBrainNames.PlatformGrainStorage,
                (services, _) => ActivatorUtilities.CreateInstance<DigitalBrain.Authority.Storage.HostedPlatformGrainStorage>(services));
            builder.Services.AddSingleton<IReminderTable, DigitalBrain.Authority.Storage.HostedReminderTable>();
        }
        builder.Services.AddIdentity();
        builder.WebHost.ConfigureKestrel(server => server.ConfigureHttpsDefaults(https =>
        {
            https.ServerCertificate = certificate;
            https.ClientCertificateMode = ClientCertificateMode.AllowCertificate;
            https.ClientCertificateValidation = (presented, _, _) => presented.GetCertHashString(HashAlgorithmName.SHA256) == certificate.GetCertHashString(HashAlgorithmName.SHA256);
        }));
        await using var app = builder.Build();
        app.Urls.Add("https://127.0.0.1:0");
        app.UsePlatformHttp();
        RuntimeManagementEndpoints.Map(app);
        IdentityEndpoints.Map(app);
        app.MapGet("/workspace-probe", () => "workspace");
        await app.StartAsync(ct);
        using var handler = new HttpClientHandler();
        handler.ClientCertificates.Add(certificate);
        handler.ServerCertificateCustomValidationCallback = (_, presented, _, _) => presented?.GetCertHashString(HashAlgorithmName.SHA256) == certificate.GetCertHashString(HashAlgorithmName.SHA256);
        using var client = new HttpClient(handler) { BaseAddress = new(app.Urls.Single()) };
        using var ensure = await client.PostAsync("/runtime/management/v1/ensure", null, ct);
        Assert.Equal(HttpStatusCode.OK, ensure.StatusCode);
        using var repeated = await client.PostAsync("/runtime/management/v1/ensure", null, ct);
        Assert.Equal(HttpStatusCode.OK, repeated.StatusCode);
        using var observation = await client.GetAsync("/runtime/management/v1/observation", ct);
        Assert.Equal(HttpStatusCode.OK, observation.StatusCode);
        var body = await observation.Content.ReadFromJsonAsync<HostedRuntimeObservation>(ct);
        Assert.Equal("hosted-brain", body!.BrainId);
        Assert.Equal("hosted-account", body.AccountId);
        Assert.Equal("allocation-one", body.RuntimeId);
        Assert.True(body.Registered);
        Assert.Equal(attachStorage, body.RuntimeHealthy);
        Assert.Equal(attachStorage, body.StorageAttached);
        using var workspace = await client.GetAsync("/workspace-probe", ct);
        Assert.Equal(HttpStatusCode.Unauthorized, workspace.StatusCode);
        using var anonymousHandler = new HttpClientHandler();
        anonymousHandler.ServerCertificateCustomValidationCallback = handler.ServerCertificateCustomValidationCallback;
        using var anonymous = new HttpClient(anonymousHandler) { BaseAddress = client.BaseAddress };
        using var denied = await anonymous.PostAsync("/runtime/management/v1/ensure", null, ct);
        Assert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode);
        using var issue = await client.PostAsJsonAsync("/runtime/management/v1/workspace", new { userId = "cloud-user", sessionId = "cloud-session" }, ct);
        Assert.Equal(HttpStatusCode.OK, issue.StatusCode);
        var issued = await issue.Content.ReadFromJsonAsync<HostedWorkspaceStore.Issued>(ct);
        anonymous.DefaultRequestHeaders.Add("Origin", client.BaseAddress!.GetLeftPart(UriPartial.Authority));
        using var exchange = await anonymous.PostAsJsonAsync("/runtime/workspace/exchange", new { code = issued!.Code }, ct);
        Assert.Equal(HttpStatusCode.OK, exchange.StatusCode);
        using var replay = await anonymous.PostAsJsonAsync("/runtime/workspace/exchange", new { code = issued.Code }, ct);
        Assert.Equal(HttpStatusCode.Unauthorized, replay.StatusCode);
        using var allowedWorkspace = await anonymous.GetAsync("/workspace-probe", ct);
        Assert.Equal(HttpStatusCode.OK, allowedWorkspace.StatusCode);
        using var createOther = await anonymous.PostAsync("/identity/brains/", null, ct);
        Assert.Equal(HttpStatusCode.Forbidden, createOther.StatusCode);
        var access = app.Services.GetRequiredService<DigitalBrain.Framework.IBrainAccess>();
        var identityClient = brain.Services.GetRequiredService<IIdentityClient>();
        await identityClient.EnsureBrain(new Member
        {
            AccountId = "hosted-account",
            PrincipalId = "hosted-account",
            BrainId = "other-brain",
            Role = MemberRole.Owner,
            DisplayName = "Other",
            JoinedAt = DateTimeOffset.UtcNow
        });
        Assert.False(await access.CanAccessAsync("hosted-account", "hosted-account", "other-brain", ct));
        using var otherBrain = await anonymous.GetAsync("/brains/other-brain/management/v1/observation", ct);
        Assert.Equal(HttpStatusCode.Forbidden, otherBrain.StatusCode);
        using var revoke = await client.PostAsJsonAsync("/runtime/management/v1/workspace/revoke", new { sessionId = "cloud-session" }, ct);
        Assert.Equal(HttpStatusCode.NoContent, revoke.StatusCode);
        using var revokedWorkspace = await anonymous.GetAsync("/workspace-probe", ct);
        Assert.Equal(HttpStatusCode.Unauthorized, revokedWorkspace.StatusCode);
        await app.StopAsync(ct);
        Directory.Delete(stateDirectory, true);
    }

    [Theory]
    [InlineData(true, true, true)]
    [InlineData(false, true, false)]
    [InlineData(true, false, false)]
    public async Task RequiresTlsAndAllocationCertificate(bool tls, bool matching, bool expected)
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest("CN=allocation-controller", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddHours(1));
        var options = new HostedRuntimeOptions
        {
            AccountId = "account",
            BrainId = "brain",
            RuntimeId = "runtime",
            ControllerCertificateSha256 = matching ? certificate.GetCertHashString(HashAlgorithmName.SHA256) : new string('0', 64)
        };
        var context = new DefaultHttpContext();
        context.Request.Scheme = tls ? "https" : "http";
        context.Connection.ClientCertificate = certificate;
        var authentication = new RuntimeMachineAuthentication(Options.Create(options), TimeProvider.System);
        Assert.Equal(expected, await authentication.AuthenticateAsync(context, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ForwardedHeaderCannotSupplyMachineIdentity()
    {
        var context = new DefaultHttpContext();
        context.Request.Scheme = "https";
        context.Request.Headers["X-Client-Cert"] = "asserted-certificate";
        var authentication = new RuntimeMachineAuthentication(Options.Create(new HostedRuntimeOptions()), TimeProvider.System);
        Assert.False(await authentication.AuthenticateAsync(context, TestContext.Current.CancellationToken));
    }
}
