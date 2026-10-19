using System.Security.Cryptography;
using System.Text.Json;
using IntoChat.OS.Authority.Storage;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Serialization;
using System.Net;
using System.IO.Compression;
using System.Security.Cryptography.X509Certificates;
using IntoChat.OS.Aspire.Server;
using IntoChat.OS.Framework;
using IntoChat.OS.Authority.Hosted;
using IntoChat.OS.Authority.Hosting;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Server.Kestrel.Https;
using Microsoft.Extensions.Hosting;

namespace IntoChat.OS.Authority.Tests.Integration.Storage;

[Trait("host", "own")]
public sealed class HostedExportFacts
{
    [GenerateSerializer, Alias("test.export.customer"), ExportableState]
    public sealed record CustomerState([property: Id(0)] string Text);

    [GenerateSerializer, Alias("test.export.unmarked")]
    public sealed record UnmarkedState([property: Id(0)] string Text);

    [GrainType("export-customer")]
    public sealed class CustomerGrain([PersistentState("note", IntoChatOSNames.ClusterGrainStorage)] IPersistentState<CustomerState> state) : Grain
    {
        public CustomerState Read() => state.State;
    }

    private sealed class MustNotStart : IHostedService
    {
        public Task StartAsync(CancellationToken cancellationToken) => throw new InvalidOperationException("Customer background execution started in recovery mode.");
        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }

    [Fact]
    public async Task ColdRecoveryHostExportsSnapshotWithoutSiloOrBackgroundExecutionAndRequiresMtls()
    {
        var ct = TestContext.Current.CancellationToken;
        var directory = Directory.CreateTempSubdirectory("cold-export-").FullName;
        var custody = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        var scope = BrainScope.Create("export-account", "export-brain").Id;
        await using (var original = await TestBrain.Create()
            .WithConfiguration(IntoChatOSNames.MasterKeyConfigurationKey, custody)
            .WithConfiguration(BrainStoreOptions.Key + ":Directory", Path.Combine(directory, "source")).StartAsync(ct))
        {
            var log = await original.Services.GetRequiredService<BrainLogs>().For(scope);
            var serializer = original.Services.GetRequiredService<Serializer>();
            await log.Append(GrainId.Create("export-customer", scope + "/note").ToString(), "note", null,
                BrainRecordChange.Write, serializer.SerializeToArray(new CustomerState("cold portable content")), [], ct);
            await log.Append("vault", "vault", null, BrainRecordChange.Write, serializer.SerializeToArray(new CustomerState("hidden-custody")), [], ct);
            var reminders = new HostedReminderTable(original.Services.GetRequiredService<IBrainJournal>(), serializer,
                global::Microsoft.Extensions.Options.Options.Create(new HostedRuntimeOptions { AccountId = "export-account", BrainId = "export-brain" }));
            await reminders.UpsertRow(new ReminderEntry
            {
                GrainId = GrainId.Create("export-customer", scope + "/note"),
                ReminderName = "never-execute-on-export",
                StartAt = DateTime.UtcNow.AddDays(1),
                Period = TimeSpan.FromDays(1)
            });
        }
        var copy = Path.Combine(directory, "copy");
        foreach (var path in Directory.EnumerateFiles(Path.Combine(directory, "source"), "*", SearchOption.AllDirectories))
        {
            var destination = Path.Combine(copy, Path.GetRelativePath(Path.Combine(directory, "source"), path));
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(path, destination);
        }
        using var key = RSA.Create(2048);
        var request = new CertificateRequest("CN=localhost", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var generated = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddHours(1));
        using var certificate = X509CertificateLoader.LoadPkcs12(generated.Export(X509ContentType.Pkcs12), null);
        var builder = WebApplication.CreateBuilder();
        var baseline = builder.Services.Where(x => x.ServiceType == typeof(IHostedService)).ToArray();
        builder.Configuration["IntoChatOS:Auth:Posture"] = "Secured";
        builder.Configuration[IntoChatOSNames.MasterKeyConfigurationKey] = custody;
        builder.Configuration[BrainStoreOptions.Key + ":Directory"] = copy;
        builder.Configuration[BrainStoreOptions.Key + ":ConfiguredCustodyAcknowledged"] = "true";
        var artifacts = Directory.CreateDirectory(Path.Combine(directory, "artifacts")).FullName;
        await File.WriteAllTextAsync(Path.Combine(artifacts, "note.txt"), "portable artifact", ct);
        builder.Configuration["IntoChatOS:Workshop:Root"] = artifacts;
        builder.Configuration[HostedRuntimeOptions.SectionName + ":AccountId"] = "export-account";
        builder.Configuration[HostedRuntimeOptions.SectionName + ":BrainId"] = "export-brain";
        builder.Configuration[HostedRuntimeOptions.SectionName + ":RuntimeId"] = "new-export-runtime";
        builder.Configuration[HostedRuntimeOptions.SectionName + ":StateDirectory"] = Path.Combine(directory, "hosting");
        builder.Configuration[HostedRuntimeOptions.SectionName + ":ControllerCertificateSha256"] = certificate.GetCertHashString(HashAlgorithmName.SHA256);
        builder.Configuration[HostedRuntimeOptions.SectionName + ":RecoveryOnly"] = "true";
        builder.AddHostedAuthority();
        builder.AddIntoChatOSServer(_ => { });
        builder.Services.AddHostedService<MustNotStart>();
        HostedRecoveryHost.Configure(builder, baseline);
        Assert.DoesNotContain(builder.Services, x => x.ServiceType == typeof(IHostedService) && !baseline.Contains(x));
        builder.WebHost.ConfigureKestrel(server => server.ConfigureHttpsDefaults(https =>
        {
            https.ServerCertificate = certificate;
            https.ClientCertificateMode = ClientCertificateMode.AllowCertificate;
            https.ClientCertificateValidation = (presented, _, _) => presented.GetCertHashString(HashAlgorithmName.SHA256) == certificate.GetCertHashString(HashAlgorithmName.SHA256);
        }));
        await using var app = builder.Build();
        app.Urls.Add("https://127.0.0.1:0");
        HostedRecoveryHost.Map(app);
        await app.StartAsync(ct);
        using var handler = new HttpClientHandler
        {
            ServerCertificateCustomValidationCallback = (_, presented, _, _) =>
            presented?.GetCertHashString(HashAlgorithmName.SHA256) == certificate.GetCertHashString(HashAlgorithmName.SHA256)
        };
        handler.ClientCertificates.Add(certificate);
        using var client = new HttpClient(handler) { BaseAddress = new(app.Urls.Single()) };
        using var response = await client.GetAsync("runtime/management/v1/export", ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var archive = new ZipArchive(new MemoryStream(await response.Content.ReadAsByteArrayAsync(ct)));
        using var reader = new StreamReader(archive.GetEntry("brain.json")!.Open());
        var json = await reader.ReadToEndAsync(ct);
        Assert.Contains("cold portable content", json);
        Assert.DoesNotContain("hidden-custody", json);
        Assert.NotNull(archive.GetEntry("artifacts/note.txt"));
        using var deniedHandler = new HttpClientHandler { ServerCertificateCustomValidationCallback = handler.ServerCertificateCustomValidationCallback };
        using var denied = new HttpClient(deniedHandler) { BaseAddress = client.BaseAddress };
        Assert.Equal(HttpStatusCode.Unauthorized, (await denied.GetAsync("runtime/management/v1/export", ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("runtime/management/v1/ensure", ct)).StatusCode);
        await app.StopAsync(ct);
    }

    [Fact]
    public async Task ExportReadsCustomerStateAndOmitsAuthorityAndCredentials()
    {
        await using var brain = await TestBrain.Create().StartAsync(TestContext.Current.CancellationToken);
        var serializer = brain.Services.GetRequiredService<Serializer>();
        using var log = new BrainLog(Directory.CreateTempSubdirectory("brain-export-").FullName, "owner", RandomNumberGenerator.GetBytes(32), serializer);
        await log.Append("customer", "state", null, BrainRecordChange.Write, serializer.SerializeToArray(new CustomerState("portable note")), [], TestContext.Current.CancellationToken);
        await log.Append("vault", "vault", null, BrainRecordChange.Write, serializer.SerializeToArray(new CustomerState("secret")), [], TestContext.Current.CancellationToken);
        var exported = log.Export((_, _, bytes) => serializer.Deserialize<CustomerState>(bytes));
        var json = JsonSerializer.Serialize(exported);
        Assert.Contains("portable note", json);
        Assert.DoesNotContain("secret", json);
        Assert.Equal(1, exported.OmittedRecords);
    }

    [Fact]
    public async Task ExportOmitsUnmarkedStateAndHardDeniedNamesEvenWhenMarked()
    {
        await using var brain = await TestBrain.Create().StartAsync(TestContext.Current.CancellationToken);
        var serializer = brain.Services.GetRequiredService<Serializer>();
        using var log = new BrainLog(Directory.CreateTempSubdirectory("brain-export-char-").FullName, "owner", RandomNumberGenerator.GetBytes(32), serializer);
        var marked = serializer.SerializeToArray(new CustomerState("portable note"));
        await log.Append("customer", "note", null, BrainRecordChange.Write, marked, [], TestContext.Current.CancellationToken);
        await log.Append("unmarked", "plain", null, BrainRecordChange.Write, serializer.SerializeToArray(new UnmarkedState("never copied")), [], TestContext.Current.CancellationToken);
        foreach (var name in StateExportPolicy.HardDenied)
        {
            await log.Append("denied-" + name, name, null, BrainRecordChange.Write, marked, [], TestContext.Current.CancellationToken);
        }
        var exported = log.Export((_, state, raw) => state == "plain"
            ? serializer.Deserialize<UnmarkedState>(raw) : serializer.Deserialize<CustomerState>(raw));
        Assert.Equal(["customer"], exported.Records.Select(x => x.GrainId).ToArray());
        Assert.DoesNotContain("never copied", JsonSerializer.Serialize(exported));
        Assert.Equal(1 + StateExportPolicy.HardDenied.Count, exported.OmittedRecords);
    }

    [Theory]
    [InlineData("vault"), InlineData("credential"), InlineData("registration"), InlineData("access"),
        InlineData("account"), InlineData("principal"), InlineData("reminders")]
    public void PolicyDeniesHardDeniedNamesEvenForMarkedTypes(string name)
    {
        Assert.False(StateExportPolicy.Default.Allows(name, typeof(CustomerState)));
        Assert.True(StateExportPolicy.Default.Allows("note", typeof(CustomerState)));
        Assert.False(StateExportPolicy.Default.Allows("note", typeof(UnmarkedState)));
    }
}
