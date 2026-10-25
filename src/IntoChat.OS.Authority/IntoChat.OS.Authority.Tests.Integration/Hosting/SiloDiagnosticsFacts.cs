using System.Net;
using System.Net.Http.Json;
using IntoChat.OS.Authority.Hosting;
using IntoChat.OS.Authority.Storage;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace IntoChat.OS.Authority.Tests.Integration.Hosting;

[Trait("host", "own")]
public sealed class SiloDiagnosticsFacts
{
    private const string Key = "a-diagnostics-key-from-the-vault";

    [Fact]
    public async Task Without_a_configured_key_the_diagnostics_are_not_mapped()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var app = await Start(key: null, Directory.CreateTempSubdirectory("diag-").FullName, ct);
        using var client = Client(app, header: Key);
        using var silo = await client.GetAsync(SiloDiagnostics.SiloPath, ct);
        Assert.Equal(HttpStatusCode.NotFound, silo.StatusCode);
        using var canary = await client.PostAsJsonAsync(SiloDiagnostics.CanaryPath, new { value = "x" }, ct);
        Assert.Equal(HttpStatusCode.NotFound, canary.StatusCode);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("wrong")]
    public async Task A_caller_without_the_key_is_refused(string? header)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var app = await Start(Key, Directory.CreateTempSubdirectory("diag-").FullName, ct);
        using var client = Client(app, header);
        using var silo = await client.GetAsync(SiloDiagnostics.SiloPath, ct);
        Assert.Equal(HttpStatusCode.Unauthorized, silo.StatusCode);
        using var read = await client.GetAsync(SiloDiagnostics.CanaryPath, ct);
        Assert.Equal(HttpStatusCode.Unauthorized, read.StatusCode);
        using var write = await client.PostAsJsonAsync(SiloDiagnostics.CanaryPath, new { value = "x" }, ct);
        Assert.Equal(HttpStatusCode.Unauthorized, write.StatusCode);
    }

    [Fact]
    public async Task The_silo_reports_its_identity_storage_mode_and_ownership_counters()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var app = await Start(Key, Directory.CreateTempSubdirectory("diag-").FullName, ct, revision: "ca-intochat-test-os--r42");
        using var client = Client(app, Key);
        using var response = await client.GetAsync(SiloDiagnostics.SiloPath, ct);
        response.EnsureSuccessStatusCode();
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
        var report = await response.Content.ReadFromJsonAsync<SiloReport>(ct);
        Assert.NotNull(report);
        Assert.Equal("default", report.ClusterId);
        Assert.Equal("ca-intochat-test-os--r42", report.Revision);
        Assert.Equal(Environment.MachineName, report.Hostname);
        Assert.Equal("local", report.Storage.Mode);
        Assert.Equal(nameof(BrainLogFormat.Framed), report.Storage.WriteFormat);
        Assert.Equal(new BrainOwnershipCounts("none", 0, 0, 0, 0, 0, 0), report.Ownership);
    }

    [Fact]
    public async Task A_registered_ownership_source_replaces_the_no_op_default()
    {
        var ct = TestContext.Current.CancellationToken;
        var counts = new BrainOwnershipCounts("blobLease", 3, 1, 0, 2, 40, 1);
        await using var app = await Start(Key, Directory.CreateTempSubdirectory("diag-").FullName, ct,
            services => services.AddSingleton<IBrainOwnershipCounters>(new FixedCounters(counts)));
        using var client = Client(app, Key);
        var report = await client.GetFromJsonAsync<SiloReport>(SiloDiagnostics.SiloPath, ct);
        Assert.Equal(counts, report!.Ownership);
    }

    [Fact]
    public async Task The_canary_written_before_a_restart_reads_back_after_it()
    {
        var ct = TestContext.Current.CancellationToken;
        var directory = Directory.CreateTempSubdirectory("diag-").FullName;
        await using (var first = await Start(Key, directory, ct, revision: "r1"))
        {
            using var client = Client(first, Key);
            using var missing = await client.GetAsync(SiloDiagnostics.CanaryPath, ct);
            Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
            using var written = await client.PostAsJsonAsync(SiloDiagnostics.CanaryPath, new { value = "deploy-41" }, ct);
            written.EnsureSuccessStatusCode();
            await first.StopAsync(ct);
        }
        await using var second = await Start(Key, directory, ct, revision: "r2");
        using var after = Client(second, Key);
        var canary = await after.GetFromJsonAsync<CanaryRecord>(SiloDiagnostics.CanaryPath, ct);
        Assert.Equal("deploy-41", canary!.Value);
        Assert.Equal("r1", canary.Revision);
    }

    [Fact]
    public async Task Ownership_counters_registered_after_the_platform_still_win()
    {
        var ct = TestContext.Current.CancellationToken;
        var counts = new BrainOwnershipCounts("blobLease", 1, 0, 0, 0, 0, 0);
        var services = new ServiceCollection();
        services.AddSiloDiagnostics();
        services.UseBrainOwnershipCounters(new FixedCounters(counts));
        services.AddSiloDiagnostics();
        await using var provider = services.BuildServiceProvider();
        Assert.Equal(counts, provider.GetRequiredService<IBrainOwnershipCounters>().Snapshot());
        Assert.Single(services, d => d.ServiceType == typeof(IBrainOwnershipCounters));
        await Task.CompletedTask.WaitAsync(ct);
    }

    [Fact]
    public async Task Parallel_canary_writes_each_succeed_and_leave_one_of_them()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var app = await Start(Key, Directory.CreateTempSubdirectory("diag-").FullName, ct);
        using var client = Client(app, Key);
        var values = Enumerable.Range(0, 8).Select(i => $"deploy-{i}").ToArray();
        var responses = await Task.WhenAll(values.Select(v => client.PostAsJsonAsync(SiloDiagnostics.CanaryPath, new { value = v }, ct)));
        foreach (var response in responses) { using (response) { response.EnsureSuccessStatusCode(); } }
        var canary = await client.GetFromJsonAsync<CanaryRecord>(SiloDiagnostics.CanaryPath, ct);
        Assert.Contains(canary!.Value, values);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task A_blank_canary_is_refused(string value)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var app = await Start(Key, Directory.CreateTempSubdirectory("diag-").FullName, ct);
        using var client = Client(app, Key);
        using var response = await client.PostAsJsonAsync(SiloDiagnostics.CanaryPath, new { value }, ct);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task An_oversized_canary_is_refused()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var app = await Start(Key, Directory.CreateTempSubdirectory("diag-").FullName, ct);
        using var client = Client(app, Key);
        using var response = await client.PostAsJsonAsync(SiloDiagnostics.CanaryPath, new { value = new string('x', SiloDiagnostics.MaxCanaryLength + 1) }, ct);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    private static async Task<WebApplication> Start(string? key, string directory, CancellationToken ct,
        Action<IServiceCollection>? configure = null, string? revision = null)
    {
        var builder = WebApplication.CreateBuilder();
        if (key is not null) { builder.Configuration[SiloDiagnostics.KeyConfiguration] = key; }
        if (revision is not null) { builder.Configuration[SiloDiagnostics.RevisionVariable] = revision; }
        builder.Services.AddOptions<BrainStoreOptions>().BindConfiguration(BrainStoreOptions.Key).Configure(options => options.Directory = directory);
        configure?.Invoke(builder.Services);
        builder.Services.AddSiloDiagnostics();
        var app = builder.Build();
        app.Urls.Add("http://127.0.0.1:0");
        app.MapSiloDiagnostics();
        await app.StartAsync(ct);
        return app;
    }

    private static HttpClient Client(WebApplication app, string? header)
    {
        var client = new HttpClient { BaseAddress = new(app.Urls.Single()) };
        if (header is not null) { client.DefaultRequestHeaders.Add(SiloDiagnostics.KeyHeader, header); }
        return client;
    }

    private sealed class FixedCounters(BrainOwnershipCounts counts) : IBrainOwnershipCounters
    {
        public BrainOwnershipCounts Snapshot() => counts;
    }
}
